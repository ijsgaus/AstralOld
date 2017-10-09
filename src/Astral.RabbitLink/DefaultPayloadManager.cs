﻿using System;
using System.Collections.ObjectModel;
using System.Net.Mime;
using Astral.Liaison;
using Astral.Payloads;
using Astral.Payloads.DataContracts;
using Astral.Payloads.Serialization;
using Newtonsoft.Json;
using RabbitLink.Messaging;

namespace Astral.RabbitLink
{
    internal class DefaultPayloadManager : IPayloadManager
    {
        private readonly Serialization<byte[]> _serialization;
        private readonly TypeEncoding _typeEncoding;

        public DefaultPayloadManager() : this(global::Astral.Payloads.Serialization.Serialization.JsonRaw)
        {
        }

        public DefaultPayloadManager(JsonSerializerSettings settings)
            :this(global::Astral.Payloads.Serialization.Serialization.MakeJsonRaw(settings))
        {
        }

        public DefaultPayloadManager(Serialization<byte[]> serialization) 
          : this(serialization, new TypeEncoding(
              TypeEncoder.Default.Fallback(TypeEncoder.KnownType<RpcFail>("rpc.fail"))
                  .Fallback(TypeEncoder.KnownType<RpcOk>("rpc.ok")).Loopback(), 
              TypeDecoder.Default.Fallback(TypeDecoder.KnownType<RpcFail>("rpc.fail"))
                  .Fallback(TypeDecoder.KnownType<RpcOk>("rpc.ok")).Loopback()))
        {
            
        }

        public DefaultPayloadManager(Serialization<byte[]> serialization, TypeEncoding typeEncoding)
        {
            _serialization = serialization;
            _typeEncoding = typeEncoding;
        }

        public byte[] Serialize<T>(ContentType defaultContentType, T body, LinkMessageProperties props)
        {
            var payload = Payload.ToPayload(new Tracer(), body,
                new PayloadEncode<byte[]>(defaultContentType, _typeEncoding.Encode, _serialization.Serialize)).Unwrap();
            props.ContentType = payload.ContentType.ToString();
            props.Type = payload.TypeCode;
            return payload.Data;
        }

        

        public object Deserialize(ILinkMessage<byte[]> message, Type awaitedType)
        {
            var payload = new Payload<byte[]>(message.Properties.Type, new ContentType(message.Properties.ContentType), message.Body);
            return Payload.FromPayload(new Tracer(), payload, new ReadOnlyCollection<Type>(new [] { awaitedType }),
                new PayloadDecode<byte[]>(_typeEncoding.Decode, _serialization.Deserialize)).Unwrap();
        }


        private class Tracer : ITracer
        {
            public void Write(string message)
            {
             
            }

            private struct EmptyDisposable : IDisposable
            {
                public void Dispose()
                {
                    
                }
            }
            
            public IDisposable Scope(string name, ushort offset = 4)
                => default(EmptyDisposable);
        }
        
        
    }
    
    
}