using System;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Astral.Configuration;
using Astral.Configuration.Builders;
using Astral.Configuration.Settings;
using Astral.Data;
using Astral.Deliveries;
using Astral.Payloads;
using Astral.Specifications;
using Astral.Transport;
using FunEx;
using FunEx.Monads;
using Lawium;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Astral.Internals
{
    public class BusService<TService> : IBusService<TService>
        where TService : class

    {

        private readonly BlockedDisposableDictionary<string, RpcSubscriber> _subscribers = new BlockedDisposableDictionary<string, RpcSubscriber>();
        private readonly ICancelable _disposable;
        
        internal BusService(ServiceConfig<TService> config)
        {
            Config = config;
            Logger = config.LoggerFactory.CreateLogger<BusService<TService>>();
            _disposable = new CompositeDisposable(Disposable.Create(() =>
            {
               _subscribers.Dispose();
            }));
        }

        public void Dispose()
        {
            _disposable.Dispose();
        }

        internal ServiceConfig<TService> Config { get; }

        public ILogger Logger { get; }



        public IEventEndpoint<TEvent> Endpoint<TEvent>(Expression<Func<TService, IEvent<TEvent>>> selector)
            => new EventEndpoint<TEvent>(Config.Endpoint(selector));
        
        

        #region ICall<>

        public async Task<Guid> Send<TCommand>(Expression<Func<TService, ICall<TCommand>>> selector, TCommand command,
            ChannelKind.RespondableChannel responseTo = null, CancellationToken cancellation = default(CancellationToken))
        {
            var endpoint = Config.Endpoint(selector);
            responseTo = responseTo ??
                         endpoint.GetRequiredService<ResponseTo>();
            
            var correlationId = Guid.NewGuid();
            var payload = endpoint.ToPayload(command).Unwrap();
            var sender = endpoint.Transport.PreparePublish<TCommand>(endpoint, false, responseTo);
            await sender(new Lazy<TCommand>(() => command), payload, correlationId.ToString("D"), cancellation);
            return correlationId;
        }
        
        public async Task Response<TCommand>(Expression<Func<TService, ICall<TCommand>>> selector,
            ChannelKind.ReplyChannel replyTo, CancellationToken cancellation = default(CancellationToken))
        {
            if (replyTo == null) throw new ArgumentNullException(nameof(replyTo));
            var endpoint = Config.Endpoint(selector);
            var payload = endpoint.ToPayload(default(ValueTuple)).Unwrap();
            var sender = endpoint.Transport.PreparePublish<ValueTuple>(endpoint, true, replyTo);
            await sender(new Lazy<ValueTuple>(() => default(ValueTuple)), payload, replyTo.RequestId, cancellation);
        }
        
        public async Task ResponseFault<TCommand>(Expression<Func<TService, ICall<TCommand>>> selector,
            RequestFault fault, ChannelKind.ReplyChannel replayTo, CancellationToken cancellation = default(CancellationToken))
        {
            if (replayTo == null) throw new ArgumentNullException(nameof(replayTo));
            var endpoint = Config.Endpoint(selector);
            var payload = endpoint.ToPayload(fault).Unwrap();
            var sender = endpoint.Transport.PreparePublish<RequestFault>(endpoint, true, replayTo);
            await sender(new Lazy<RequestFault>(() => fault), payload, replayTo.RequestId, cancellation);
        }
        
        public Task<Guid> Deliver<TStore, TCommand>(IUnitOfWork<TStore> uow, Expression<Func<TService, ICall<TCommand>>> selector,
            TCommand command, DeliveryOnSuccess? onSuccess = null, ChannelKind.DurableChannel replyTo = null)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, command, false,
                DeliveryReply.WithReply(replyTo ?? endpoint.GetRequiredService<DeliveryReplayTo>()),
                onSuccess ?? endpoint.GetRequiredService<DeliveryOnSuccess>());
        }
        
        

        public Task<Guid> Enqueue<TStore, TCommand>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TCommand>>> selector, TCommand command, ChannelKind.DurableChannel replyTo = null)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, command, false,
                DeliveryReply.WithReply(replyTo ?? endpoint.GetRequiredService<DeliveryReplayTo>()), Option.None);
        }

        public Task<Guid> DeliverResponse<TStore, TCommand>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TCommand>>> selector,
            ChannelKind.ReplyChannel replayTo)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, default(ValueTuple), true,
                DeliveryReply.IsReply(replayTo),
                DeliveryOnSuccess.Delete);
        }
        
        public Task<Guid> DeliverFaultReply<TStore, TCommand>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TCommand>>> selector, RequestFault fault,
            ChannelKind.ReplyChannel replayTo)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, fault, true,
                DeliveryReply.IsReply(replayTo),
                DeliveryOnSuccess.Delete);
        }

        public IDisposable ListenResponse<TCommand>(Expression<Func<TService, ICall<TCommand>>> selector,
            Func<Result<ValueTuple>, ResponseContext, CancellationToken, Task> listener,
            ChannelKind.DurableChannel replyFrom = null, Action<ChannelBuilder> configure = null)
        {
            var endpoint = Config.Endpoint(selector);
            var channel = endpoint.Channel(replyFrom ?? ChannelKind.System, true, configure ?? (_ => { }));
            
            return Listen(channel, listener,
                p =>
                {
                    var fromP = endpoint.FromPayload(p);
                    return fromP.As<ValueTuple>()
                        .BiBind<ValueTuple, ValueTuple>(h => h.ToOk(), ex => fromP.As<RequestFault>().Match(t => new FaultException(t.Message, t.Code).ToFail<ValueTuple>(),
                            ex1 => ex.FlatCombine(ex)));
                },
                p => new ResponseContext(p.Sender, p.RequestId));
        }

        public IDisposable ListenRequest<TCommand>(Expression<Func<TService, ICall<TCommand>>> selector,
            Func<TCommand, RequestContext, CancellationToken, Task> listener, Action<ChannelBuilder> configure = null)
        {
            var endpoint = Config.Endpoint(selector);
            var channel = endpoint.Channel(ChannelKind.System, false, configure);
            return Listen(channel, listener, p =>
            {
                var reply =
                    !string.IsNullOrWhiteSpace(p.ReplayTo) && !string.IsNullOrWhiteSpace(p.RequestId)
                        ? ChannelKind.Reply(p.ReplayTo, p.RequestId)
                        : null;
                return new RequestContext(p.Sender, reply, new Responder<TService, TCommand>(this, selector, reply));
            });
        }
        
        public async Task Call<TCommand>(
            Expression<Func<TService, ICall<TCommand>>> selector,
            TCommand request, TimeSpan? timeout = null)
        {
            var endpoint = Config.Endpoint(selector);
            var tout = timeout ?? endpoint.GetRequiredService<RpcTimeout>();
            var req = endpoint.ToPayload(request).Unwrap();
            if (endpoint.Transport is IRpcTransport rpc)
            {
                
                await rpc.Call(endpoint, new Lazy<TCommand>(() => request), req, tout);
                return;
            }
            var channel = endpoint.Channel(ChannelKind.Rpc, true, _ => { });
            var (name, subscribable) = endpoint.Transport.GetChannel(channel);
            var rpcSubscriber = _subscribers.GetOrAdd(name, _ => new RpcSubscriber(subscribable));
            var requestId = Guid.NewGuid().ToString("D");
            using (var cancellationSource = new CancellationTokenSource(tout))
            {
                var token = cancellationSource.Token;
                var task = rpcSubscriber.AnswerAsync(requestId, token);
                await endpoint.Transport.PreparePublish<TCommand>(endpoint, false,
                    ChannelKind.Rpc)(new Lazy<TCommand>(() => request),  req, requestId, token);
                await task;
            }
        }
        
        public IDisposable HandleCall<TCommand>(
            Expression<Func<TService, ICall<TCommand>>> selector,
            Func<TCommand, CancellationToken, Task> handler)
        {
            return ListenRequest(selector, async (req, ctx, token) =>
            {
                try
                {
                    await handler(req, token);
                }
                catch (Exception ex)
                {
                    await ctx.Response.Send(new RequestFault {Code = ex.GetType().Name, Message = ex.Message}, token);
                    return;
                }
                await ctx.Response.Send(token);
            });
        }

        #endregion
        
        #region ICall<,>
        
        public async Task<Guid> Send<TRequest, TResponse>(Expression<Func<TService, ICall<TRequest, TResponse>>> selector, TRequest command,
            ChannelKind.RespondableChannel responseTo = null, CancellationToken cancellation = default(CancellationToken))
        {
            var endpoint = Config.Endpoint(selector);
            responseTo = responseTo ??
                         endpoint.GetRequiredService<ResponseTo>();
            var correlationId = Guid.NewGuid();
            var payload = endpoint.ToPayload(command).Unwrap();
            var sender = endpoint.Transport.PreparePublish<TRequest>(endpoint, false, responseTo);
            await sender(new Lazy<TRequest>(() => command), payload, correlationId.ToString("D"), cancellation);
            return correlationId;
        }
        
        public async Task Response<TRequest, TResponse>(Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            TResponse response, ChannelKind.ReplyChannel replayTo, CancellationToken cancellation = default(CancellationToken))
        {
            if (replayTo == null) throw new ArgumentNullException(nameof(replayTo));
            var endpoint = Config.Endpoint(selector);
            var payload = endpoint.ToPayload(response).Unwrap();
            var sender = endpoint.Transport.PreparePublish<TResponse>(endpoint, true, replayTo);
            await sender(new Lazy<TResponse>(() => response), payload, replayTo.RequestId, cancellation);
        }
        
        public async Task ResponseFault<TRequest, TResponse>(Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            RequestFault fault, ChannelKind.ReplyChannel replayTo, CancellationToken cancellation = default(CancellationToken))
        {
            if (replayTo == null) throw new ArgumentNullException(nameof(replayTo));
            var endpoint = Config.Endpoint(selector);
            var payload = endpoint.ToPayload(fault).Unwrap();
            var sender = endpoint.Transport.PreparePublish<RequestFault>(endpoint, true, replayTo);
            await sender(new Lazy<RequestFault>(() => fault), payload, replayTo.RequestId, cancellation);
        }
        
        public Task<Guid> Deliver<TStore, TRequest, TResponse>(IUnitOfWork<TStore> uow, Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            TRequest command, DeliveryOnSuccess? onSuccess = null, ChannelKind.DurableChannel replyTo = null)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, command, false,
                DeliveryReply.WithReply(replyTo ?? endpoint.GetRequiredService<DeliveryReplayTo>()),
                onSuccess ?? endpoint.GetRequiredService<DeliveryOnSuccess>());
        }

        public Task<Guid> Enqueue<TStore, TRequest, TResponse>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TRequest, TResponse>>> selector, TRequest command, ChannelKind.DurableChannel replyTo = null)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, command, false,
                DeliveryReply.WithReply(replyTo ?? endpoint.GetRequiredService<DeliveryReplayTo>()), Option.None);
        }

        public Task<Guid> DeliverResponse<TStore, TRequest, TReplay>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TRequest, TReplay>>> selector, TReplay response,
            ChannelKind.ReplyChannel replyTo)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, response, true,
                DeliveryReply.IsReply(replyTo),
                DeliveryOnSuccess.Delete);
        }
        
        public Task<Guid> DeliverFaultReply<TStore, TRequest, TReplay>(IUnitOfWork<TStore> uow,
            Expression<Func<TService, ICall<TRequest, TReplay>>> selector, RequestFault fault,
            ChannelKind.ReplyChannel replayTo)
        {
            var endpoint = Config.Endpoint(selector);
            return Deliverer(uow, endpoint, fault, true,
                DeliveryReply.IsReply(replayTo),
                DeliveryOnSuccess.Delete);
        }
        
        public IDisposable ListenResponse<TRequest, TResponse>(Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            Func<Result<TResponse>, ResponseContext, CancellationToken, Task> listener,
            ChannelKind.DurableChannel replyFrom = null, Action<ChannelBuilder> configure = null)
        {
            var endpoint = Config.Endpoint(selector);
            var channel = endpoint.Channel(replyFrom ?? ChannelKind.System, true, configure ?? (_ => { }));
            
            return Listen(channel, listener, p =>
            {
                var fromP = endpoint.FromPayload(p);
                return fromP.As<TResponse>()
                    .BiBind(h => h.ToOk(), ex => fromP.As<RequestFault>().Match(t => new FaultException(t.Message, t.Code).ToFail<TResponse>(),
                            ex1 => ex.FlatCombine(ex)));
            },  p => new ResponseContext(p.Sender, p.RequestId));
        }
        
        public IDisposable ListenRequest<TRequest, TResponse>(Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            Func<TRequest, RequestContext<TResponse>, CancellationToken, Task> listener, Action<ChannelBuilder> configure = null)
        {
            var endpoint = Config.Endpoint(selector);
            var channel = endpoint.Channel(ChannelKind.System, false, configure);
            return Listen(channel, listener, p =>
            {
                var reply =
                    !string.IsNullOrWhiteSpace(p.ReplayTo) && !string.IsNullOrWhiteSpace(p.RequestId)
                        ? ChannelKind.Reply(p.ReplayTo, p.RequestId)
                        : null;
                return new RequestContext<TResponse>(p.Sender, reply, new Responder<TService, TRequest, TResponse>(this, selector, reply));
            });
        }

        public async Task<TResponse> Call<TRequest, TResponse>(
            Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            TRequest request, TimeSpan? timeout = null)
        {
            var endpoint = Config.Endpoint(selector);
            var tout = timeout ?? endpoint.GetRequiredService<RpcTimeout>();
            var req = endpoint.ToPayload(request).Unwrap();
            if (endpoint.Transport is IRpcTransport rpc)
            {
                
                var resp = await rpc.Call(endpoint, new Lazy<TRequest>(() => request), req, tout);
                return endpoint.FromPayload(resp).As<TResponse>().Unwrap();
            }
            var channel = endpoint.Channel(ChannelKind.Rpc, true, _ => { });
            var (name, subscribable) = endpoint.Transport.GetChannel(channel);
            var rpcSubscriber = _subscribers.GetOrAdd(name, _ => new RpcSubscriber(subscribable));
            var requestId = Guid.NewGuid().ToString(":D");
            using (var cancellationSource = new CancellationTokenSource(tout))
            {
                var token = cancellationSource.Token;
                var task = rpcSubscriber.AnswerAsync(requestId, token);
                await endpoint.Transport.PreparePublish<TRequest>(endpoint, false,
                    ChannelKind.Rpc)(new Lazy<TRequest>(() => request),  req, requestId, token);
                var resp = await task;
                return endpoint.FromPayload(resp).As<TResponse>().Unwrap();
            }
        }

        public IDisposable HandleCall<TRequest, TResponse>(
            Expression<Func<TService, ICall<TRequest, TResponse>>> selector,
            Func<TRequest, CancellationToken, Task<TResponse>> handler)
        {
            return ListenRequest(selector, async (req, ctx, token) =>
            {
                TResponse resp;
                try
                {
                    resp = await handler(req, token);
                }
                catch (Exception ex)
                {
                    await ctx.Response.Send(new RequestFault {Code = ex.GetType().Name, Message = ex.Message}, token);
                    return;
                }
                await ctx.Response.Send(resp, token);
            });
        }
        
        
        
        #endregion


        private async Task<Guid> Deliverer<TStore, TMessage>(IUnitOfWork<TStore> uow, EndpointConfig endpoint, TMessage message,
            bool isReply, DeliveryReply reply, Option<DeliveryOnSuccess> onSuccess)
        {
            var deliveryId = Guid.NewGuid();
            var deliveryManager = Config.GetRequiredService<BoundDeliveryManager<TStore>>();
            var channel = reply.Match(() => (ChannelKind.RespondableChannel) ChannelKind.None, rt => rt, rt => rt);
            var sender = endpoint.Transport.PreparePublish<TMessage>(endpoint, isReply, channel);
            await deliveryManager.Prepare(uow, endpoint, deliveryId, message, reply, sender, onSuccess);
            return deliveryId;
        }
        
        
        
        private Task PublishMessageAsync<TEvent>(EndpointConfig config, TEvent @event, ChannelKind channel, CancellationToken token)
        {
            Task Publish()
            {
                var serialized = config.ToPayload(@event).Unwrap();
                var prepared = config.Transport.PreparePublish<TEvent>(config, false, channel);
                return prepared(new Lazy<TEvent>(() => @event), serialized, null, token);
            }

            return Logger.LogActivity(Publish, "event {service} {endpoint}", config.ServiceType,
                config.PropertyInfo.Name);
        }


        private IDisposable Listen<TEvent, TContext>(ChannelConfig config,
            Func<TEvent, TContext, CancellationToken, Task> eventListener,
            Func<MessageContext, TContext> contextConverter)
            => Listen(config, eventListener, p => config.Endpoint.FromPayload(p).As<TEvent>().Unwrap(), contextConverter);
       
        private IDisposable Listen<TEvent, TContext>(ChannelConfig config,
            Func<TEvent, TContext, CancellationToken, Task> eventListener, Func<Payload<byte[]>, TEvent> converter, 
            Func<MessageContext, TContext> contextConverter)
        {
            return Logger.LogActivity(Listen, "listen {service} {endpoint}", config.Endpoint.ServiceType,
                config.Endpoint.PropertyInfo.Name);

            IDisposable Listen()
            {
                var exceptionPolicy = config.Endpoint.AsTry<ExceptionToAcknowledge>().RecoverTo(p => CommonLaws.DefaultExceptionPolicy(p));


                var (_, subscribable) = config.Endpoint.Transport.GetChannel(config);
                return subscribable((msg, ctx, token) => Listener(msg, ctx, token, ex => exceptionPolicy(ex)));
            }

            async Task<Acknowledge> Listener(
                Payload<byte[]> msg, MessageContext ctx, CancellationToken token, Func<Exception, Acknowledge> exceptionPolicy)
            {
                async Task<Acknowledge> Receive()
                {
                    var obj = converter(msg); 
                    await eventListener(obj, contextConverter(ctx), token);
                    return Acknowledge.Ack;
                }

                return await Receive()
                    .LogResult(Logger, "recive event {service} {endpoint}", config.Endpoint.ServiceType, config.Endpoint.PropertyInfo)
                    .CorrectError(exceptionPolicy);
            }
        }

        

        
    }
}