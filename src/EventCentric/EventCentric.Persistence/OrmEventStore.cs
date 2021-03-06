﻿using EventCentric.EventSourcing;
using EventCentric.Handling;
using EventCentric.Log;
using EventCentric.Messaging;
using EventCentric.Polling;
using EventCentric.Serialization;
using EventCentric.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;

namespace EventCentric.Persistence
{
    public class OrmEventStore<T> : EventStoreBase, ISubscriptionRepository, IEventStore<T> where T : class, IEventSourced
    {
        private long eventCollectionVersion = 0;
        private readonly ILogger log;
        private readonly ITextSerializer serializer;
        private readonly IUtcTimeProvider time;
        private readonly IGuidProvider guid;
        private readonly ObjectCache cache;

        private readonly Func<Guid, IEnumerable<IEvent>, T> aggregateFactory;
        private readonly Func<Guid, ISnapshot, T> originatorAggregateFactory;

        private readonly Func<bool, IEventStoreDbContext> contextFactory;
        private readonly Action<T, IEventStoreDbContext> denormalizeIfApplicable;
        private readonly Func<IEvent, DateTime, InboxEntity> inboxEntityFactory;
        private readonly Func<string, ITextSerializer, string, bool> consumerFilter;

        private readonly object dbLock = new object();

        public OrmEventStore(string streamType, ITextSerializer serializer, Func<bool, IEventStoreDbContext> contextFactory, IUtcTimeProvider time, IGuidProvider guid, ILogger log, bool persistIncomingPayloads, Func<string, ITextSerializer, string, bool> consumerFilter)
            : base(streamType)
        {
            Ensure.NotNull(serializer, nameof(serializer));
            Ensure.NotNull(contextFactory, nameof(contextFactory));
            Ensure.NotNull(time, nameof(time));
            Ensure.NotNull(guid, nameof(guid));
            Ensure.NotNull(log, nameof(log));

            this.serializer = serializer;
            this.contextFactory = contextFactory;
            this.time = time;
            this.guid = guid;
            this.log = log;
            this.cache = new MemoryCache(streamType);
            this.consumerFilter = consumerFilter != null ? consumerFilter : EventStoreFuncs.DefaultFilter;

            /// TODO: could be replaced with a compiled lambda to make it more performant.
            var fromMementoConstructor = typeof(T).GetConstructor(new[] { typeof(Guid), typeof(ISnapshot) });
            Ensure.CastIsValid(fromMementoConstructor, "Type T must have a constructor with the following signature: .ctor(Guid, IMemento)");
            this.originatorAggregateFactory = (id, memento) => (T)fromMementoConstructor.Invoke(new object[] { id, memento });

            var fromStreamConstructor = typeof(T).GetConstructor(new[] { typeof(Guid), typeof(IEnumerable<IEvent>) });
            Ensure.CastIsValid(fromStreamConstructor, "Type T must have a constructor with the following signature: .ctor(Guid, IEnumerable<IEvent>)");
            this.aggregateFactory = (id, streamOfEvents) => (T)fromStreamConstructor.Invoke(new object[] { id, streamOfEvents });

            if (typeof(IDenormalizer).IsAssignableFrom(typeof(T)))
                this.denormalizeIfApplicable = (aggregate, context) => ((IDenormalizer)aggregate).UpdateReadModel(context);
            else
                this.denormalizeIfApplicable = (aggregate, context) => { };

            using (var context = this.contextFactory.Invoke(false))
            {
                // getting event collection version
                if (context.Events.Any(e => e.StreamType == this.streamType))
                    this.eventCollectionVersion = context.Events.Where(e => e.StreamType == this.streamType).Max(e => e.EventCollectionVersion);

                // adding subscription if missing
                if (!context.Subscriptions.Any(s => s.SubscriberStreamType == this.streamType && s.StreamType == this.streamType + Constants.AppEventStreamNameSufix))
                {
                    // We should add the new subscription
                    context.Subscriptions.Add(new SubscriptionEntity
                    {
                        SubscriberStreamType = this.streamType,
                        StreamType = this.streamType + Constants.AppEventStreamNameSufix,
                        Url = "none",
                        Token = "#token",
                        ProcessorBufferVersion = 0,
                        IsPoisoned = false,
                        WasCanceled = true,
                        CreationLocalTime = DateTime.Now,
                        UpdateLocalTime = DateTime.Now
                    });

                    context.SaveChanges();
                }
            }
            this.CurrentEventCollectionVersion = this.eventCollectionVersion;

            if (persistIncomingPayloads)
                this.inboxEntityFactory = (IEvent incomingEvent, DateTime localNow) => new InboxEntity
                {
                    InboxStreamType = this.streamType,
                    EventId = incomingEvent.EventId,
                    TransactionId = incomingEvent.TransactionId,
                    StreamType = incomingEvent.StreamType,
                    StreamId = incomingEvent.StreamId,
                    Version = incomingEvent.Version,
                    EventType = incomingEvent.GetType().Name,
                    EventCollectionVersion = incomingEvent.EventCollectionVersion,
                    CreationLocalTime = localNow,
                    Payload = this.serializer.Serialize(incomingEvent)
                };
            else
                this.inboxEntityFactory = (IEvent incomingEvent, DateTime localNow) => new InboxEntity
                {
                    InboxStreamType = this.streamType,
                    EventId = incomingEvent.EventId,
                    TransactionId = incomingEvent.TransactionId,
                    CreationLocalTime = localNow
                };
        }

        public T Find(Guid id)
        {
            // get memento from cache
            var cachedMemento = (Tuple<ISnapshot, DateTime?>)this.cache.Get(id.ToString());
            if (cachedMemento == null || !cachedMemento.Item2.HasValue)
            {
                // try return memento from SQL Server;
                using (var context = this.contextFactory.Invoke(true))
                {
                    var snapshotEntity = context.Snapshots.Where(s => s.StreamId == id && s.StreamType == this.streamType).SingleOrDefault();

                    if (snapshotEntity != null)
                        cachedMemento = new Tuple<ISnapshot, DateTime?>(this.serializer.Deserialize<ISnapshot>(snapshotEntity.Payload), null);
                    else
                        return this.GetFromFullStreamOfEvents(id, context);
                }
            }

            try
            {
                return this.originatorAggregateFactory.Invoke(id, cachedMemento.Item1);
            }
            catch (Exception ex)
            {
                this.log.Error(ex, $"An error ocurred while hydrating aggregate from snapshot. Now we will try to recover state from full stream of events for stream id {id.ToString()}");
                using (var context = this.contextFactory.Invoke(true))
                {
                    return this.GetFromFullStreamOfEvents(id, context);
                }
            }
        }

        private T GetFromFullStreamOfEvents(Guid id, IEventStoreDbContext context)
        {
            // if memento not found then try get full stream
            var streamOfEvents = context.Events
                           .Where(e => e.StreamId == id && e.StreamType == this.streamType)
                           .OrderBy(e => e.Version)
                           .AsEnumerable()
                           .Select(e => this.serializer.Deserialize<IEvent>(e.Payload))
                           .AsCachedAnyEnumerable();

            if (streamOfEvents.Any())
                return this.aggregateFactory.Invoke(id, streamOfEvents);

            return null;
        }

        public T Get(Guid id)
        {
            var aggregate = this.Find(id);
            if (aggregate == null)
            {
                var ex = new StreamNotFoundException(id, streamType);
                this.log.Error(ex, string.Format("Stream not found exception for stream {0} with id of {1}", streamType, id));
                throw ex;
            }

            return aggregate;
        }

        public void Save(T eventSourced, IEvent incomingEvent)
        {
            string key = null;

            try
            {
                using (var context = this.contextFactory.Invoke(false))
                {
                    // Check if incoming event is duplicate, if is not a cloaked event
                    if (context.Inbox.Any(e => e.EventId == incomingEvent.EventId))
                        // Incoming event is duplicate
                        return;

                    var now = this.time.Now;
                    var localNow = this.time.Now.ToLocalTime();

                    if (eventSourced == null)
                    {
                        context.Inbox.Add(this.inboxEntityFactory.Invoke(incomingEvent, localNow));
                        context.SaveChanges();
                        return;
                    }

                    if (eventSourced.Id == default(Guid))
                        throw new ArgumentOutOfRangeException("StreamId", $"The eventsourced of type {typeof(T).FullName} has a default GUID value for its stream id, which is not valid");

                    var pendingEvents = eventSourced.PendingEvents;
                    if (pendingEvents.Count > 0)
                    {
                        var currentVersion = context.Events.Any(e => e.StreamId == eventSourced.Id && e.StreamType == this.streamType)
                      ? context.Events
                        .Where(e => e.StreamId == eventSourced.Id && e.StreamType == this.streamType)
                        .Max(e => e.Version)
                      : 0;

                        if (currentVersion + 1 != pendingEvents.First().Version)
                            throw new EventStoreConcurrencyException();

                        // Cache Memento And Publish Stream
                        var snapshot = ((ISnapshotOriginator)eventSourced).SaveToSnapshot();

                        // Cache in Sql Server
                        var serializedMemento = this.serializer.Serialize(snapshot);

                        var streamEntity = context.Snapshots.Where(s => s.StreamId == eventSourced.Id && s.StreamType == this.streamType).SingleOrDefault();
                        if (streamEntity != null)
                        {
                            streamEntity.Version = eventSourced.Version;
                            streamEntity.Payload = serializedMemento;
                            streamEntity.UpdateLocalTime = localNow;
                        }
                        else
                        {
                            streamEntity = new SnapshotEntity
                            {
                                StreamType = this.streamType,
                                StreamId = eventSourced.Id,
                                Version = eventSourced.Version,
                                Payload = serializedMemento,
                                CreationLocalTime = localNow,
                                UpdateLocalTime = localNow
                            };
                            context.Snapshots.Add(streamEntity);
                        }

                        // Cache in memory
                        key = eventSourced.Id.ToString();
                        this.cache.Set(
                            key: key,
                            value: new Tuple<ISnapshot, DateTime?>(snapshot, now),
                            policy: new CacheItemPolicy { AbsoluteExpiration = this.time.OffSetNow.AddMinutes(30) });
                    }

                    // Log the incoming message in the inbox
                    context.Inbox.Add(this.inboxEntityFactory.Invoke(incomingEvent, localNow));

                    // Denormalize if applicable.
                    this.denormalizeIfApplicable(eventSourced, context);


                    List<EventEntity> eventEntities = new List<EventEntity>();
                    foreach (var @event in pendingEvents)
                    {
                        var e = (Message)@event;
                        e.TransactionId = incomingEvent.TransactionId;
                        e.EventId = this.guid.NewGuid();
                        e.StreamType = this.streamType;

                        eventEntities.Add(
                            new EventEntity
                            {
                                StreamType = this.streamType,
                                StreamId = @event.StreamId,
                                Version = @event.Version,
                                EventId = @event.EventId,
                                TransactionId = @event.TransactionId,
                                EventType = @event.GetType().Name,
                                CorrelationId = incomingEvent.EventId,
                                LocalTime = localNow,
                                UtcTime = now
                            });
                    }

                    lock (this.dbLock)
                    {
                        var eventCollectionBeforeCrash = this.eventCollectionVersion;
                        try
                        {
                            for (int i = 0; i < pendingEvents.Count; i++)
                            {
                                this.eventCollectionVersion += 1;
                                var @event = pendingEvents[i];
                                ((Message)@event).EventCollectionVersion = this.eventCollectionVersion;
                                var entity = eventEntities[i];
                                entity.EventCollectionVersion = this.eventCollectionVersion;
                                entity.Payload = this.serializer.Serialize(@event);
                                context.Events.Add(entity);
                            }
                            context.SaveChanges();

                            this.CurrentEventCollectionVersion = this.eventCollectionVersion;
                        }
                        catch (Exception ex)
                        {
                            this.eventCollectionVersion = eventCollectionBeforeCrash;
                            throw ex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.log.Error(ex, $"An error ocurred while storing events while processing incoming event of type '{incomingEvent.GetType().Name}'");

                // Mark cache as stale
                if (key != null)
                {
                    var item = (Tuple<ISnapshot, DateTime?>)this.cache.Get(key);
                    if (item != null && item.Item2.HasValue)
                    {
                        item = new Tuple<ISnapshot, DateTime?>(item.Item1, null);
                        this.cache.Set(
                            key,
                            item,
                            new CacheItemPolicy { AbsoluteExpiration = this.time.OffSetNow.AddMinutes(30) });
                    }
                }

                throw;
            }
        }

        public bool IsDuplicate(Guid eventId, out Guid transactionId)
        {
            transactionId = default(Guid);
            using (var context = this.contextFactory.Invoke(true))
            {
                var duplicate = context.Inbox.SingleOrDefault(x => x.EventId == eventId);
                if (duplicate == null)
                    return false;

                transactionId = duplicate.TransactionId;
                return true;
            }
        }

        public void DeleteSnapshot(Guid streamId)
        {
            cache.Remove(streamId.ToString());
            using (var context = this.contextFactory.Invoke(false))
            {
                context.Snapshots.Remove(context.Snapshots.Single(x => x.StreamId == streamId));
                context.SaveChanges();
            }
        }

        public long CurrentEventCollectionVersion { get; private set; }

        public string StreamName => this.streamType;

        public SerializedEvent[] FindEventsForConsumer(long from, long to, int quantity, string consumer)
        {
            using (var context = this.contextFactory(true))
            {
                return context
                        .Events
                        .Where(e => e.StreamType == this.streamType && e.EventCollectionVersion > from && e.EventCollectionVersion <= to)
                        .OrderBy(e => e.EventCollectionVersion)
                        .Take(quantity)
                        .ToArray()
                        .Select(e =>
                            EventStoreFuncs.ApplyConsumerFilter(
                                new SerializedEvent(e.EventCollectionVersion, e.Payload),
                                consumer,
                                this.serializer,
                                this.consumerFilter))
                        .ToArray();
            }
        }

        public SerializedEvent[] FindEventsForConsumer(long from, long to, Guid streamId, int quantity, string consumer)
        {
            using (var context = this.contextFactory(true))
            {
                var events = context
                .Events
                .Where(e => e.StreamType == this.streamType
                    && e.EventCollectionVersion > from
                    && e.EventCollectionVersion <= to
                    && e.StreamId == streamId)
                .OrderBy(e => e.EventCollectionVersion)
                .Take(quantity)
                .ToArray()
                .Select(e =>
                    EventStoreFuncs.ApplyConsumerFilter(
                        new SerializedEvent(e.EventCollectionVersion, e.Payload),
                        consumer,
                        this.serializer,
                        this.consumerFilter))
                .ToArray();

                return events.Length > 0
                ? events
                : new SerializedEvent[] { new SerializedEvent(to, this.serializer.Serialize(new CloakedEvent(to, this.streamType))) };
            }
        }

        public SubscriptionBuffer[] GetSubscriptions()
        {
            var subscriptions = new List<SubscriptionBuffer>();

            using (var context = this.contextFactory(true))
            {
                var subscriptionsQuery = context.Subscriptions.Where(s => s.SubscriberStreamType == this.streamType && !s.IsPoisoned && !s.WasCanceled);
                if (subscriptionsQuery.Any())
                    foreach (var s in subscriptionsQuery)
                        // We substract one version in order to set the current version bellow the last one, in case that first event was not yet processed.
                        subscriptions.Add(new SubscriptionBuffer(s.StreamType.Trim(), s.Url.Trim(), s.Token, s.ProcessorBufferVersion - 1, s.IsPoisoned));
            }

            return subscriptions.ToArray();
        }

        public void FlagSubscriptionAsPoisoned(IEvent poisonedEvent, PoisonMessageException exception)
        {
            using (var context = this.contextFactory.Invoke(false))
            {
                var subscription = context.Subscriptions.Where(s => s.StreamType == poisonedEvent.StreamType && s.SubscriberStreamType == this.streamType).Single();
                subscription.IsPoisoned = true;
                subscription.UpdateLocalTime = this.time.Now.ToLocalTime();
                subscription.PoisonEventCollectionVersion = poisonedEvent.EventCollectionVersion;
                try
                {
                    subscription.ExceptionMessage = this.serializer.Serialize(exception);
                }
                catch (Exception)
                {
                    subscription.ExceptionMessage = string.Format("Exception type: {0}. Exception message: {1}. Inner exception: {2}", exception.GetType().Name, exception.Message, exception.InnerException.Message != null ? exception.InnerException.Message : "null");
                }
                try
                {
                    subscription.DeadLetterPayload = this.serializer.Serialize(poisonedEvent);
                }
                catch (Exception)
                {
                    subscription.DeadLetterPayload = string.Format("EventType: {0}", poisonedEvent.GetType().Name);
                }

                context.SaveChanges();
            }
        }

        public bool TryAddNewSubscriptionOnTheFly(string streamType, string url, string token)
        {
            using (var context = this.contextFactory.Invoke(false))
            {
                if (context.Subscriptions.Any(s => s.SubscriberStreamType == this.streamType && s.StreamType == streamType))
                    return false;

                var now = DateTime.Now;
                context.Subscriptions.Add(new SubscriptionEntity
                {
                    SubscriberStreamType = this.streamType,
                    StreamType = streamType,
                    Url = url,
                    Token = token,
                    CreationLocalTime = now,
                    UpdateLocalTime = now
                });

                context.SaveChanges();
                return true;
            }
        }

        public void PersistSubscriptionVersion(string subscription, long consumerVersion, long producerVersion)
        {
            using (var context = this.contextFactory.Invoke(false))
            {
                var sub = context.Subscriptions.Single(x => x.SubscriberStreamType == this.streamType && x.StreamType == subscription);
                sub.ProcessorBufferVersion = consumerVersion;
                sub.ProducerVersion = producerVersion;
                sub.ConsistencyPercentage = base.GetConsistencyPercentage(consumerVersion, producerVersion);
                sub.UpdateLocalTime = DateTime.Now;
                context.SaveChanges();
            }
        }
    }
}
