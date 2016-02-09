﻿using EventCentric.Repository;
using System;
using System.Collections.Generic;

namespace EventCentric.EventSourcing
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

        public void Denormalize(IEventStoreDbContext context)
        {
            this.denormalize((TDbContext)context);
        }

        public TAggregate UpdateReadModel(Action<TDbContext> denormalize)
        {
            this.denormalize = denormalize;
            return base.Update(new ReadModelUpdated());
        }

        public void On(ReadModelUpdated e)
        { }
    }
}
