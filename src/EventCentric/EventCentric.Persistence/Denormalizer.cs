﻿using EventCentric.EventSourcing;
using System;
using System.Collections.Generic;

namespace EventCentric.Persistence.SqlServer
{
    public abstract class Denormalizer<TAggregate, TDbContext> : EventSourced<TAggregate>, IDenormalizer
        where TAggregate : class, IEventSourced
        where TDbContext : IEventStoreDbContext
    {
        private Action<TDbContext> denormalize = context => { };

        protected Denormalizer(Guid id)
            : base(id)
        { }

        protected Denormalizer(Guid id, IEnumerable<IEvent> streamOfEvents)
            : base(id, streamOfEvents)
        { }

        protected Denormalizer(Guid id, ISnapshot memento)
            : base(id, memento)
        { }

        public void UpdateReadModel(IEventStoreDbContext context)
        {
            this.denormalize((TDbContext)context);
        }

        public TAggregate Denormalize(Action<TDbContext> denormalize)
        {
            this.denormalize = denormalize;
            return this as TAggregate;
        }
    }
}
