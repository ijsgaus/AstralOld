﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Astral.Core;
using Astral.Exceptions;
using LanguageExt;
using static LanguageExt.Prelude;

namespace Astral.DataContracts
{
    public class DefaultTypeMapper : ITypeToContractName, IContractNameToType
    {
        private readonly Func<string, string> _typeNameConverter;

        public DefaultTypeMapper(Func<string, string> typeNameConverter = null)
        {
            _typeNameConverter = typeNameConverter;
        }

        private static Type TryGetElementType(Type arrayLikeType)
        {
            if (arrayLikeType.IsArray)
            {
                var elementType = arrayLikeType.GetElementType();
                return elementType;
            }
            var enumIntf = arrayLikeType.GetInterfaces()
                .FirstOrDefault(p => p.IsGenericType && p.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumIntf != null)
            {
                var elementType = enumIntf.GetGenericArguments()[0];
                return elementType;
            }
            return null;
        }

        public string Map(Type type, object data)
        {
            if (WellKnownTypes.UnitTypes.Any(p => p == type))
                return WellKnownTypes.UnitTypeCode;
            if (WellKnownTypes.CodeByType.TryGetValue(type, out var code))
                return code;
            var elementType = TryGetElementType(type);
            if(elementType != null)
                return $"{Map(elementType, null)}[]";

            type = data?.GetType() ?? type;
            var attr = type.GetCustomAttribute<ContractAttribute>();
            if (attr != null)
                return attr.Name;
            if (_typeNameConverter != null)
                return _typeNameConverter(type.Name);
            throw new TypeResolutionException(type);
        }

        public Try<Type> TryMap(string contractName, Type awaitedType)
        {
            if(string.IsNullOrWhiteSpace(contractName))
                return Try<Type>(new DataContractResolutionException(null));
            if (contractName == WellKnownTypes.UnitTypeCode)
                return Try(WellKnownTypes.UnitTypes[0]);
            if(WellKnownTypes.TypeByCode.TryGetValue(contractName, out var type))
                return Try(type);
            if (contractName.EndsWith("[]") )
            {
                var elementType = TryGetElementType(awaitedType);

                var elementName = contractName.Remove(contractName.Length - 2);
                return TryMap(elementName, elementType).Map(p => p.MakeArrayType());
            }
            if(awaitedType == null)
                return Try<Type>(new DataContractResolutionException(contractName));
            if(CheckSubtype(awaitedType)) return Try(awaitedType);
            var attr = awaitedType.GetCustomAttribute<ContractAttribute>();
            if (attr?.Name == contractName)
                return Try(awaitedType);
            var attrs = awaitedType.GetCustomAttributes<KnownTypeAttribute>();
            foreach (var known in attrs)
            {
                if (known.MethodName != null)
                {
                    var method = awaitedType.GetMethod(known.MethodName);
                    var subTypes = (IEnumerable<Type>)method.Invoke(null, new object[0]);
                    var found = subTypes.FirstOrDefault(CheckSubtype);
                    if (found != null) return Try(found);
                }
                else if (CheckSubtype(known.Type)) return Try(known.Type);
            }

            return Try<Type>(new DataContractResolutionException(contractName));

            bool CheckSubtype(Type t)
            {
                var a1 = t.GetCustomAttribute<ContractAttribute>();
                if (a1 != null)
                    return a1.Name == contractName;
                if (_typeNameConverter != null)
                    return _typeNameConverter(t.Name) == contractName;
                return false;
            }

        }
    }
}