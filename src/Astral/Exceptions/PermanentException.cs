﻿using System;

namespace Astral.Exceptions
{
    public class PermanentException : Exception
    {
        public PermanentException()
        {
        }

        public PermanentException(string message) : base(message)
        {
        }

        public PermanentException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}