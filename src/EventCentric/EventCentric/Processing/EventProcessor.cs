﻿using EventCentric.EventSourcing;
using EventCentric.Messaging;
using EventCentric.Messaging.Commands;
using EventCentric.Messaging.Events;
using EventCentric.Utils;
using System;
using System.Collections.Concurrent;

namespace EventCentric.Processing
{
    /// <summary>
    /// An Event Processor
    /// </summary>
    /// <typeparam name="T">The type of the <see cref="IEventSourced"/> aggregate.</typeparam>
    /// <remarks>
    /// If the aggregate needs to process a command and needs a Domain Service, it should be injected with 
    /// a custum made container, with a generic injection for dependency resolution. It should be best 
    /// initialiezed lazyly, as the logger of Greg Young's Event Store.
    /// </remarks>
    public class EventProcessor<T> : Worker,
        IMessageHandler<StartEventProcessor>,
        IMessageHandler<StopEventProcessor>,
        IMessageHandler<NewIncomingEvent>,
        IMessageHandler<IncomingMessageIsPoisoned>
            where T : class, IEventSourced
    {
        private readonly IEventStore<T> store;
        private readonly ISubscriptionWriter subscriptionWriter;
        private readonly Func<Guid, T> newAggregateFactory;
        protected ConcurrentDictionary<Guid, object> streamLocksById;

        public EventProcessor(IBus bus, IEventStore<T> store, ISubscriptionWriter subscriptionWriter)
            : base(bus)
        {
            Ensure.NotNull(store, "store");
            Ensure.NotNull(subscriptionWriter, "inboxWriter");

            this.store = store;
            this.subscriptionWriter = subscriptionWriter;

            this.streamLocksById = new ConcurrentDictionary<Guid, object>();

            // New aggregate
            var constructor = typeof(T).GetConstructor(new[] { typeof(Guid) });
            Ensure.CastIsValid(constructor, "Type T must have a constructor with the following signature: .ctor(Guid)");

            this.newAggregateFactory = (id) => (T)constructor.Invoke(new object[] { id });
        }

        public void Handle(NewIncomingEvent message)
        {
            try
            {
                ((dynamic)this).Handle((dynamic)message.Event);
            }
            catch (Exception ex)
            {
                this.bus.Publish(new IncomingMessageIsPoisoned(message.Event.StreamType, message.Event.StreamId, new PoisonMessageException("Poison message detected in Event Processor", ex)));
            }
        }

        public void Handle(StartEventProcessor message)
        {
            base.Start();
            base.bus.Publish(new EventProcessorStarted());
        }

        public void Handle(StopEventProcessor message)
        {
            base.Stop();
            this.bus.Publish(new EventProcessorStopped());
        }

        protected void CreateNewStream(IEvent @event)
        {
            this.HandleSafelyWithStreamLocking(Guid.Empty, () =>
            {
                var aggregate = this.store.Find(Guid.Empty);
                if (aggregate == null)
                    aggregate = this.newAggregateFactory(Guid.Empty);
                this.HandleEventAndAppendToStore(aggregate, @event);
            });
        }

        /// <summary>
        /// Creates a new stream in the store.
        /// </summary>
        /// <param name="id">The id of the new stream. A brand new computed <see cref="Guid"/>.</param>
        /// <param name="@event">The first message that the new aggregate will process.</param>
        protected void HandleFirstEvent(Guid id, IEvent @event)
        {
            this.HandleSafelyWithStreamLocking(id, () =>
            {
                var aggregate = this.newAggregateFactory(id);
                this.HandleEventAndAppendToStore(aggregate, @event);
                this.bus.Publish(new NewSubscriptionAcquired(@event.StreamType, id));
            });
        }

        /// <summary>
        /// Gets a stream from the store to hydrate the event sourced aggregate of <see cref="T"/>.
        /// </summary>
        /// <param name="id">The id of the stream.</param>
        /// <param name="@event">The event to be handled by the aggregate of <see cref="T"/>.</param>
        protected void Handle(Guid id, IEvent @event)
        {
            this.HandleSafelyWithStreamLocking(id, () =>
            {
                var aggregate = this.store.Get(id);
                this.HandleEventAndAppendToStore(aggregate, @event);
            });
        }

        public void Handle(IncomingMessageIsPoisoned message)
        {
            this.subscriptionWriter.LogPosisonedMessage(message.StreamType, message.StreamId, message.Exception);
        }

        /// <summary>
        /// Mark as ignored in the inbox table and in the subscription table. 
        /// This node is not subscribed to this event, but is interested in other events that
        /// happened in the source.
        /// </summary>
        /// <param name="@event">The <see cref="IEvent"/> to be igonred.</param>
        protected void Ignore(IEvent @event)
        {
            this.subscriptionWriter.LogIncomingEventAsIgnored(@event);
            this.PublishIncomingEventHasBeenProcessed(@event);
        }

        /// <summary>
        /// Handles and event and updates the stream
        /// </summary>
        /// <param name="aggregate">The aggregate.</param>
        /// <param name="@event">The event.</param>
        /// <returns>The updated stream version.</returns>
        private void HandleEventAndAppendToStore(T aggregate, IEvent @event)
        {
            ((dynamic)aggregate).Handle((dynamic)@event);
            this.store.Save(aggregate, @event);
            this.bus.Publish(new EventStoreHasBeenUpdated(aggregate.Id, aggregate.Version));
            this.PublishIncomingEventHasBeenProcessed(@event);
        }

        /// <summary>
        /// Safely handles the message with locking.
        /// </summary>
        /// <param name="id">The id of the stream, for locking purposes.</param>
        /// <param name="handle">The handling action.</param>
        private void HandleSafelyWithStreamLocking(Guid id, Action handle)
        {
            this.streamLocksById.TryAdd(id, new object());
            lock (this.streamLocksById.TryGetValue(id))
            {
                handle();
            }
        }

        private void PublishIncomingEventHasBeenProcessed(IEvent @event)
        {
            this.bus.Publish(new IncomingEventHasBeenProcessed(@event.StreamId, @event.StreamType, @event.Version));
        }
    }
}
