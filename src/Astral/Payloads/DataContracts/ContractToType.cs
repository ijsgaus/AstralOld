﻿using System;
using System.Collections.Immutable;
using CsFun;

namespace Astral.Payloads.DataContracts
{
    public delegate Result<Type> ContractToType(string contract, ImmutableList<Type> awaited);
}