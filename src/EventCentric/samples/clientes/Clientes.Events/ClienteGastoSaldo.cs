﻿using EventCentric.Queueing;
using System;

namespace Clientes.Events
{
    public class ClienteGastoSaldo : QueuedEvent
    {
        public ClienteGastoSaldo(int monto, Guid idCliente)
            : base(idCliente)
        {
            this.IdCliente = idCliente;
            this.Monto = monto;
        }

        public Guid IdCliente { get; private set; }
        public int Monto { get; private set; }
    }
}