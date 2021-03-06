﻿using System;
using System.Collections.Generic;
using Astral.Logging;
using Astral.RabbitLink.Descriptions;
using Astral.RabbitLink.Internals;
using Astral.RabbitLink.Logging;
using Microsoft.Extensions.Logging;
using RabbitLink;
using RabbitLink.Builders;
using RabbitLink.Connection;
using RabbitLink.Serialization;

namespace Astral.RabbitLink
{
    public class ServiceLinkBuilder : BuilderBase, IServiceLinkBuilder
    {
        private readonly ILinkBuilder _linkBuilder;
        
        
        public ServiceLinkBuilder() : base(new Dictionary<string, object>
        {
            { nameof(DescriptionFactory), new DefaultDescriptionFactory(false) },
            { nameof(PayloadManager), new DefaultLinkPayloadManager()}
        })
        {
            _linkBuilder = 
                LinkBuilder.Configure
                .AutoStart(AutoStart())
                .Timeout(Timeout())
                .RecoveryInterval(RecoveryInterval())
                .UseBackgroundThreadsForConnection(UseBackgroundThreadsForConnection());
            
        }

        private ServiceLinkBuilder(ILinkBuilder linkBuilder, IReadOnlyDictionary<string, object> store) : base(store)
        {
            _linkBuilder = linkBuilder ?? throw new ArgumentNullException(nameof(linkBuilder));
        }


        public string ConnectionName() => GetParameter(nameof(ConnectionName), (string) null);
        public IServiceLinkBuilder ConnectionName(string value)
            => new ServiceLinkBuilder(_linkBuilder.ConnectionName(value), SetParameter(nameof(ConnectionName), value));

        
        public IServiceLinkBuilder Uri(string value)
            => Uri(value == null ? null : new Uri(value));

        public Uri Uri() => GetParameter(nameof(Uri), (Uri) null);
        public IServiceLinkBuilder Uri(Uri value)
            => new ServiceLinkBuilder(_linkBuilder.Uri(value), SetParameter(nameof(Uri), value));

        public bool AutoStart() => GetParameter(nameof(AutoStart), true);
        public IServiceLinkBuilder AutoStart(bool value)
            => new ServiceLinkBuilder(_linkBuilder.AutoStart(value), SetParameter(nameof(AutoStart), value));


        public TimeSpan Timeout() => GetParameter(nameof(Timeout), TimeSpan.FromSeconds(10));
        public IServiceLinkBuilder Timeout(TimeSpan value)
            => new ServiceLinkBuilder(_linkBuilder.Timeout(value), SetParameter(nameof(Timeout), value));

        public TimeSpan RecoveryInterval() => GetParameter(nameof(RecoveryInterval), TimeSpan.FromSeconds(10));
        public IServiceLinkBuilder RecoveryInterval(TimeSpan value)
            => new ServiceLinkBuilder(_linkBuilder.RecoveryInterval(value), SetParameter(nameof(RecoveryInterval), value));

        public string HolderName() => GetParameter(nameof(HolderName), (string) null);
        public IServiceLinkBuilder HolderName(string value)
            => new ServiceLinkBuilder(_linkBuilder.AppId(value), SetParameter(nameof(HolderName), value));

        public LinkStateHandler<LinkConnectionState> OnStateChange() =>
            GetParameter(nameof(OnStateChange), (LinkStateHandler<LinkConnectionState>) null);
        public IServiceLinkBuilder OnStateChange(LinkStateHandler<LinkConnectionState> handler)
            => new ServiceLinkBuilder(_linkBuilder.OnStateChange(handler), SetParameter(nameof(OnStateChange), handler));

        public bool UseBackgroundThreadsForConnection() => GetParameter(nameof(UseBackgroundThreadsForConnection), false);
        public IServiceLinkBuilder UseBackgroundThreadsForConnection(bool value)
            => new ServiceLinkBuilder(_linkBuilder.UseBackgroundThreadsForConnection(value),
                SetParameter(nameof(UseBackgroundThreadsForConnection), value));


        public ILinkPayloadManager PayloadManager() => GetParameter(nameof(PayloadManager), (ILinkPayloadManager) null);
        public IServiceLinkBuilder PayloadManager(ILinkPayloadManager value)
            => new ServiceLinkBuilder(_linkBuilder, SetParameter(nameof(PayloadManager), value));

        public IDescriptionFactory DescriptionFactory() =>
            GetParameter(nameof(DescriptionFactory), (IDescriptionFactory) null);
        public IServiceLinkBuilder DescriptionFactory(IDescriptionFactory value)
            => new ServiceLinkBuilder(_linkBuilder, SetParameter(nameof(DescriptionFactory), value));

        
        public IServiceLinkBuilder Serializer(ILinkSerializer serializer)
            => new ServiceLinkBuilder(_linkBuilder.Serializer(serializer), Store);


        /*public IServiceLinkBuilder LoggerFactory(ILoggerFactory value)
            => LogFactory(new LogFactoryAdapter(value));*/

        public ILogFactory LogFactory() => GetParameter(nameof(LogFactory), (ILogFactory) null);
        

        public IServiceLinkBuilder LogFactory(ILogFactory value)
            => new ServiceLinkBuilder(_linkBuilder,
                SetParameter(nameof(LogFactory), value));

        public IServiceLink Build()
        {
            var logFactory = LogFactory() ?? new NullLogFactory();
            _linkBuilder.LoggerFactory(new LoggerFactoryAdapter(logFactory));
            return new ServiceLink(_linkBuilder.Build(), PayloadManager(), DescriptionFactory(), HolderName(),
                logFactory);
        }
            
    }
}