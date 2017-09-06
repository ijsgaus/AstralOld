﻿using System;
using System.Linq;
using System.Linq.Expressions;
using Astral.Configuration.Settings;
using Lawium;
using Microsoft.Extensions.Logging;

namespace Astral.Configuration.Builders
{
    public abstract class ServiceBuilder : BuilderBase
    {
        protected ServiceBuilder(LawBookBuilder bookBuilder) : base(bookBuilder)
        {
        }
    }

    public class ServiceBuilder<TService> : ServiceBuilder
    {
        internal ServiceBuilder(LawBookBuilder bookBuilder) : base(bookBuilder)
        {
        }

        public EventEndpointBuilder<TEvent> Endpoint<TEvent>(Expression<Func<TService, IEvent<TEvent>>> selector)
        {
            var propertyInfo = selector.GetProperty();
            var builder = BookBuilder.GetSubBookBuilder(propertyInfo.Name,
                b => b.AddEndpointLaws(propertyInfo));
            return new EventEndpointBuilder<TEvent>(builder);
        }

        public CallEndpointBuilder<TArgs> Endpoint<TArgs>(Expression<Func<TService, ICall<TArgs>>> selector)
        {
            var propertyInfo = selector.GetProperty();
            var builder = BookBuilder.GetSubBookBuilder(propertyInfo.Name,
                b => b.AddEndpointLaws(propertyInfo));
            return new CallEndpointBuilder<TArgs>(builder);
        }

        public CallEndpointBuilder<TArgs, TResult> Endpoint<TArgs, TResult>(
            Expression<Func<TService, ICall<TArgs, TResult>>> selector)
        {
            var propertyInfo = selector.GetProperty();
            var builder = BookBuilder.GetSubBookBuilder(propertyInfo.Name,
                b => b.AddEndpointLaws(propertyInfo));
            return new CallEndpointBuilder<TArgs, TResult>(builder);
        }
    }
}