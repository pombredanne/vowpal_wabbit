﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ReflectionHelper.cs">
//   Copyright (c) by respective owners including Yahoo!, Microsoft, and
//   individual contributors. All rights reserved.  Released under a BSD
//   license as described in the file LICENSE.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace VW.Serializer.Reflection
{
    /// <summary>
    /// Reflection helper to find methods on visitors.
    /// </summary>
    internal static class ReflectionHelper
    {
        /// <summary>
        /// TODO: replace me with Roslyn once it's released and just generate string code. This way the overload resolution is properly done.
        /// </summary>
        /// <remarks>This is a simple heuristic for overload resolution, not the full thing.</remarks>
        internal static MethodInfo FindMethod(Type objectType, string name, Type valueType)
        {
            Contract.Ensures(objectType != null);
            Contract.Ensures(name != null);
            Contract.Ensures(valueType != null);

            // let's find the "best" match:
            // order by 
            //  1. distance (0 = assignable, 1 = using generic) --> ascending
            //  2. # of interfaces implemented. the more the better (the more specific we are) --> descending
            //  3. # of open generics. the less the better (the more specific we are) --> ascending
            var methods = from m in objectType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                          where m.Name == name
                          let parameters = m.GetParameters()
                          where parameters.Length == 1
                          let methodParameter = parameters[0].ParameterType
                          let output = new
                          {
                              Method = m,
                              Distance = Distance(methodParameter, valueType)
                          }
                          where output.Distance != null
                          orderby
                           output.Distance.Distance,
                           output.Distance.InterfacesImplemented descending,
                           output.Distance.GenericTypes.Count
                          select output;

            var bestCandidate = methods.FirstOrDefault();
            if (bestCandidate == null)
            {
                throw new MissingMethodException("Not method found matching: " + valueType);
            }

            MethodInfo method = bestCandidate.Method;

            //foreach (var item in methods)
            //{
            //    Debug.WriteLine(string.Format("Distance={0} Interfaces={1} OpenGenerics={2} Method={3}",
            //        item.Distance.Distance,
            //        item.Distance.InterfacesImplemented,
            //        item.Distance.GenericTypes.Count,
            //        item.Method));
            //}

            if (method.IsGenericMethod)
            {
                var actualTypes = method
                    .GetGenericArguments()
                    .Select(t => bestCandidate.Distance.GenericTypes[t])
                    .ToArray();

                method = method.MakeGenericMethod(actualTypes);
                //Debug.WriteLine("\t specializing: " + method);
            }
            //Debug.WriteLine("Method: {0} for {1} {2}", method, name, valueType);

            return method;
        }

        internal static TypeMatch Distance(Type candidate, Type valueType)
        {
            if (candidate.IsAssignableFrom(valueType))
            {
                return new TypeMatch(0);
            }

            if (candidate.IsGenericParameter && candidate.GetGenericParameterConstraints().All(c => c.IsAssignableFrom(valueType)))
            {
                return new TypeMatch(1, candidate, valueType);
            }

            if (candidate.IsGenericType)
            {
                // try to find a match that is assignable... 
                // 
                var genericCandidate = candidate.GetGenericTypeDefinition();

                var bestMatches =
                    from typeDistance in valueType.GetInterfaces().Select(it => new TypeDistance { Distance = 1, Type = it })
                                .Union(GetBaseTypes(valueType))
                    let type = typeDistance.Type
                    where type.IsGenericType && type.GetGenericTypeDefinition() == genericCandidate
                    let distances = candidate.GetGenericArguments().Zip(type.GetGenericArguments(), (a, b) => Distance(a, b)).ToList()
                    where distances.All(d => d != null)
                    let output = new TypeMatch(typeDistance.Distance, distances)
                    {
                        InterfacesImplemented = distances.Sum(d => d.InterfacesImplemented)
                                + (candidate.IsInterface ? candidate.GetInterfaces().Count() : 0)
                    }
                    orderby output.Distance, output.InterfacesImplemented descending, output.GenericTypes.Count
                    select output;

                return bestMatches.FirstOrDefault();
            }

            return null;
        }

        internal static IEnumerable<TypeDistance> GetBaseTypes(Type type, int depth = 0)
        {
            if (type == typeof(object) || type == null)
            {
                yield break;
            }

            yield return new TypeDistance { Type = type, Distance = depth };

            foreach (var item in GetBaseTypes(type.BaseType, depth + 1))
            {
                yield return item;
            }
        }

        /// <summary>
        /// Gets the member info in a sort of type safe manner - it's better than using strings, but some runtime errors are still possbile.
        /// </summary>
        public static MemberInfo GetInfo<T, TResult>(Expression<Func<T, TResult>> expression)
        {
            return GetInfo(expression.Body);
        }

        /// <summary>
        /// Gets the member info in a sort of type safe manner - it's better than using strings, but some runtime errors are still possbile.
        /// </summary>
        public static MemberInfo GetInfo<T>(Expression<Action<T>> expression)
        {
            return GetInfo(expression.Body);
        }

        /// <summary>
        /// Gets the member info in a sort of type safe manner - it's better than using strings, but some runtime errors are still possbile.
        /// </summary>
        public static MemberInfo GetInfo(Expression expression)
        {
            var binaryExpression = expression as BinaryExpression;
            if (binaryExpression != null)
            {
                if (binaryExpression.Method != null)
                {
                    return binaryExpression.Method;
                }
                
                throw new NotSupportedException();
            }
            
            var methodExpression = expression as MemberExpression;
            if (methodExpression != null)
            {
                return methodExpression.Member;
            }

            var methodCallExpression = expression as MethodCallExpression;
            if (methodCallExpression != null)
            {
                return methodCallExpression.Method;
            }

            var newExpression = expression as NewExpression;
            if (newExpression != null)
            {
                return newExpression.Constructor;
            }

            var unaryExpression = expression as UnaryExpression;
            if (unaryExpression != null)
            {
                if (unaryExpression.Method != null)
                {
                    return unaryExpression.Method;
                }
            }

            throw new NotSupportedException();
        }
    }
}
