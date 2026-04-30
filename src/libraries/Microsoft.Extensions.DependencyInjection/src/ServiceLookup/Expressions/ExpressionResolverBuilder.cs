// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal sealed class ExpressionResolverBuilder : CallSiteVisitor<object?, Expression>
    {
        private static readonly ParameterExpression ScopeParameter = Expression.Parameter(typeof(ServiceProviderEngineScope));

        private static readonly ParameterExpression ResolvedServices = Expression.Variable(typeof(IDictionary<ServiceCacheKey, object>), ScopeParameter.Name + "resolvedServices");
        private static readonly ParameterExpression Sync = Expression.Variable(typeof(object), ScopeParameter.Name + "sync");
        private static readonly BinaryExpression ResolvedServicesVariableAssignment =
            Expression.Assign(ResolvedServices,
                Expression.Property(
                    ScopeParameter,
                    typeof(ServiceProviderEngineScope).GetProperty(nameof(ServiceProviderEngineScope.ResolvedServices), BindingFlags.Instance | BindingFlags.NonPublic)!));

        private static readonly BinaryExpression SyncVariableAssignment =
            Expression.Assign(Sync,
                Expression.Property(
                    ScopeParameter,
                    typeof(ServiceProviderEngineScope).GetProperty(nameof(ServiceProviderEngineScope.Sync), BindingFlags.Instance | BindingFlags.NonPublic)!));

        private static readonly ConstantExpression CallSiteRuntimeResolverInstanceExpression = Expression.Constant(
            CallSiteRuntimeResolver.Instance,
            typeof(CallSiteRuntimeResolver));

        // ValueTask<object?> reflection
        private static readonly ConstructorInfo ValueTaskObjectCtor =
            typeof(ValueTask<object?>).GetConstructor(new[] { typeof(object) })!;

        private static readonly PropertyInfo ValueTaskIsCompletedSuccessfullyProp =
            typeof(ValueTask<object?>).GetProperty(nameof(ValueTask<object?>.IsCompletedSuccessfully))!;

        private static readonly PropertyInfo ValueTaskResultProp =
            typeof(ValueTask<object?>).GetProperty(nameof(ValueTask<object?>.Result))!;

        private static readonly MethodInfo GetSynchronousResultObjectMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.GetSynchronousResult))!
                .MakeGenericMethod(typeof(object));

        private static readonly MethodInfo AwaitConstructorMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitConstructor), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo AwaitArrayElementsMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitArrayElements), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo AwaitAndCaptureDisposableMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitAndCaptureDisposable), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly ServiceProviderEngineScope _rootScope;

        private readonly ConcurrentDictionary<ServiceCacheKey, Func<ServiceProviderEngineScope, ValueTask<object?>>> _scopeResolverCache;

        private readonly Func<ServiceCacheKey, ServiceCallSite, Func<ServiceProviderEngineScope, ValueTask<object?>>> _buildTypeDelegate;

        public ExpressionResolverBuilder(ServiceProvider serviceProvider)
        {
            _rootScope = serviceProvider.Root;
            _scopeResolverCache = new ConcurrentDictionary<ServiceCacheKey, Func<ServiceProviderEngineScope, ValueTask<object?>>>();
            _buildTypeDelegate = (key, cs) => BuildNoCache(cs);
        }

        public Func<ServiceProviderEngineScope, ValueTask<object?>> Build(ServiceCallSite callSite)
        {
            if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
            {
#if NETFRAMEWORK || NETSTANDARD2_0
                return _scopeResolverCache.GetOrAdd(callSite.Cache.Key, key => _buildTypeDelegate(key, callSite));
#else
                return _scopeResolverCache.GetOrAdd(callSite.Cache.Key, _buildTypeDelegate, callSite);
#endif
            }

            return BuildNoCache(callSite);
        }

        private Func<ServiceProviderEngineScope, ValueTask<object?>> BuildNoCache(ServiceCallSite callSite)
        {
            Expression<Func<ServiceProviderEngineScope, ValueTask<object?>>> expression = BuildExpression(callSite);
            DependencyInjectionEventSource.Log.ExpressionTreeGenerated(_rootScope.RootProvider, callSite.ServiceType, expression);
            return expression.Compile();
        }

        private Expression<Func<ServiceProviderEngineScope, ValueTask<object?>>> BuildExpression(ServiceCallSite callSite)
        {
            if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
            {
                return Expression.Lambda<Func<ServiceProviderEngineScope, ValueTask<object?>>>(
                    Expression.Block(
                        new[] { ResolvedServices, Sync },
                        ResolvedServicesVariableAssignment,
                        SyncVariableAssignment,
                        BuildScopedExpression(callSite)),
                    ScopeParameter);
            }

            return Expression.Lambda<Func<ServiceProviderEngineScope, ValueTask<object?>>>(
                VisitCallSite(callSite, null),
                ScopeParameter);
        }

        private static NewExpression WrapInValueTask(Expression objectExpression)
        {
            return Expression.New(ValueTaskObjectCtor,
                Convert(objectExpression, typeof(object), forceValueTypeConversion: true));
        }

        protected override Expression VisitRootCache(ServiceCallSite singletonCallSite, object? context)
        {
            return WrapInValueTask(
                Expression.Constant(CallSiteRuntimeResolver.Instance.Resolve(singletonCallSite, _rootScope)));
        }

        protected override Expression VisitConstant(ConstantCallSite constantCallSite, object? context)
        {
            return WrapInValueTask(Expression.Constant(constantCallSite.DefaultValue));
        }

        protected override Expression VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, object? context)
        {
            return WrapInValueTask(ScopeParameter);
        }

        protected override Expression VisitFactory(FactoryCallSite factoryCallSite, object? context)
        {
            return WrapInValueTask(
                Expression.Invoke(Expression.Constant(factoryCallSite.Factory), ScopeParameter));
        }

        protected override Expression VisitIEnumerable(IEnumerableCallSite callSite, object? context)
        {
            [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
                Justification = "VerifyAotCompatibility ensures elementType is not a ValueType")]
            static MethodInfo GetArrayEmptyMethodInfo(Type elementType)
            {
                Debug.Assert(!ServiceProvider.VerifyAotCompatibility || !elementType.IsValueType, "VerifyAotCompatibility=true will throw during building the IEnumerableCallSite if elementType is a ValueType.");

                return ServiceLookupHelpers.GetArrayEmptyMethodInfo(elementType);
            }

            [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
                Justification = "VerifyAotCompatibility ensures elementType is not a ValueType")]
            static NewArrayExpression NewArrayInit(Type elementType, IEnumerable<Expression> expr)
            {
                Debug.Assert(!ServiceProvider.VerifyAotCompatibility || !elementType.IsValueType, "VerifyAotCompatibility=true will throw during building the IEnumerableCallSite if elementType is a ValueType.");

                return Expression.NewArrayInit(elementType, expr);
            }

            if (callSite.ServiceCallSites.Length == 0)
            {
                return WrapInValueTask(
                    Expression.Constant(
                        GetArrayEmptyMethodInfo(callSite.ItemType)
                        .Invoke(obj: null, parameters: Array.Empty<object>())));
            }

            int count = callSite.ServiceCallSites.Length;

            ParameterExpression[] vtVars = new ParameterExpression[count];
            Expression[] body = new Expression[count + 1];

            for (int i = 0; i < count; i++)
            {
                vtVars[i] = Expression.Variable(typeof(ValueTask<object?>), $"elemVt{i}");
                body[i] = Expression.Assign(vtVars[i], VisitCallSite(callSite.ServiceCallSites[i], context));
            }

            Expression allCompleted = Expression.Property(vtVars[0], ValueTaskIsCompletedSuccessfullyProp);
            for (int i = 1; i < count; i++)
            {
                allCompleted = Expression.AndAlso(allCompleted,
                    Expression.Property(vtVars[i], ValueTaskIsCompletedSuccessfullyProp));
            }

            Expression fastPath = WrapInValueTask(
                NewArrayInit(
                    callSite.ItemType,
                    vtVars.Select(vt => Convert(
                        Expression.Property(vt, ValueTaskResultProp),
                        callSite.ItemType))));

            Expression slowPath = Expression.Call(
                AwaitArrayElementsMethod,
                Expression.Constant(callSite.ItemType),
                NewValueTaskArray(vtVars));

            body[count] = Expression.Condition(allCompleted, fastPath, slowPath);

            return Expression.Block(typeof(ValueTask<object?>), vtVars, body);
        }

        protected override Expression VisitDisposeCache(ServiceCallSite callSite, object? context)
        {
            Expression inner = VisitCallSiteMain(callSite, context);

            if (!callSite.CaptureDisposable)
            {
                return inner;
            }

            ParameterExpression vtVar = Expression.Variable(typeof(ValueTask<object?>), "disposeVt");

            Expression fastPath = WrapInValueTask(
                Expression.Call(
                    ScopeParameter,
                    ServiceLookupHelpers.CaptureDisposableMethodInfo,
                    Expression.Property(vtVar, ValueTaskResultProp)));

            Expression slowPath = Expression.Call(
                AwaitAndCaptureDisposableMethod,
                vtVar,
                ScopeParameter);

            return Expression.Block(
                typeof(ValueTask<object?>),
                new[] { vtVar },
                Expression.Assign(vtVar, inner),
                Expression.Condition(
                    Expression.Property(vtVar, ValueTaskIsCompletedSuccessfullyProp),
                    fastPath,
                    slowPath));
        }

        protected override Expression VisitConstructor(ConstructorCallSite callSite, object? context)
        {
            ParameterInfo[] parameters = callSite.ConstructorInfo.GetParameters();

            if (callSite.ParameterCallSites.Length == 0)
            {
                Expression newExpr = Expression.New(callSite.ConstructorInfo);
                if (callSite.ImplementationType!.IsValueType)
                {
                    newExpr = Expression.Convert(newExpr, typeof(object));
                }

                return WrapInValueTask(newExpr);
            }

            int count = callSite.ParameterCallSites.Length;
            ParameterExpression[] vtVars = new ParameterExpression[count];
            Expression[] body = new Expression[count + 1];

            for (int i = 0; i < count; i++)
            {
                vtVars[i] = Expression.Variable(typeof(ValueTask<object?>), $"paramVt{i}");
                body[i] = Expression.Assign(vtVars[i], VisitCallSite(callSite.ParameterCallSites[i], context));
            }

            Expression allCompleted = Expression.Property(vtVars[0], ValueTaskIsCompletedSuccessfullyProp);
            for (int i = 1; i < count; i++)
            {
                allCompleted = Expression.AndAlso(allCompleted,
                    Expression.Property(vtVars[i], ValueTaskIsCompletedSuccessfullyProp));
            }

            Expression[] fastArgs = new Expression[count];
            for (int i = 0; i < count; i++)
            {
                fastArgs[i] = Convert(
                    Expression.Property(vtVars[i], ValueTaskResultProp),
                    parameters[i].ParameterType);
            }

            Expression construct = Expression.New(callSite.ConstructorInfo, fastArgs);
            if (callSite.ImplementationType!.IsValueType)
            {
                construct = Expression.Convert(construct, typeof(object));
            }

            Expression fastPath = WrapInValueTask(construct);

            Expression slowPath = Expression.Call(
                AwaitConstructorMethod,
                Expression.Constant(callSite.ConstructorInfo),
                NewValueTaskArray(vtVars));

            body[count] = Expression.Condition(allCompleted, fastPath, slowPath);

            return Expression.Block(typeof(ValueTask<object?>), vtVars, body);
        }

        private static Expression Convert(Expression expression, Type type, bool forceValueTypeConversion = false)
        {
            // Don't convert if the expression is already assignable
            if (type.IsAssignableFrom(expression.Type)
                && (!expression.Type.IsValueType || !forceValueTypeConversion))
            {
                return expression;
            }

            return Expression.Convert(expression, type);
        }

        [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
            Justification = "ValueTask<object?> is a well-known BCL type; array creation is always safe")]
        private static NewArrayExpression NewValueTaskArray(ParameterExpression[] variables)
        {
            return Expression.NewArrayInit(typeof(ValueTask<object?>), variables);
        }

        protected override Expression VisitScopeCache(ServiceCallSite callSite, object? context)
        {
            Func<ServiceProviderEngineScope, ValueTask<object?>> lambda = Build(callSite);
            return Expression.Invoke(Expression.Constant(lambda), ScopeParameter);
        }

        // Move off the main stack
        private ConditionalExpression BuildScopedExpression(ServiceCallSite callSite)
        {
            ConstantExpression callSiteExpression = Expression.Constant(
                callSite,
                typeof(ServiceCallSite));

            // For root scope, delegate to RuntimeResolver.ResolveAsync which returns ValueTask<object?> directly.
            MethodCallExpression resolveRootScopeExpression = Expression.Call(
                CallSiteRuntimeResolverInstanceExpression,
                ServiceLookupHelpers.ResolveAsyncCallSiteAndScopeMethodInfo,
                callSiteExpression,
                ScopeParameter);

            ConstantExpression keyExpression = Expression.Constant(
                callSite.Cache.Key,
                typeof(ServiceCacheKey));

            ParameterExpression resolvedVariable = Expression.Variable(typeof(object), "resolved");

            ParameterExpression resolvedServices = ResolvedServices;

            MethodCallExpression tryGetValueExpression = Expression.Call(
                resolvedServices,
                ServiceLookupHelpers.TryGetValueMethodInfo,
                keyExpression,
                resolvedVariable);

            // VisitCallSiteMain returns ValueTask<object?>; unwrap synchronously under the lock.
            Expression serviceVtExpression = VisitCallSiteMain(callSite, null);
            Expression unwrappedService = Expression.Call(GetSynchronousResultObjectMethod, serviceVtExpression);

            Expression assignExpression;
            if (callSite.CaptureDisposable)
            {
                assignExpression = Expression.Assign(resolvedVariable,
                    Expression.Call(ScopeParameter, ServiceLookupHelpers.CaptureDisposableMethodInfo, unwrappedService));
            }
            else
            {
                assignExpression = Expression.Assign(resolvedVariable, unwrappedService);
            }

            MethodCallExpression addValueExpression = Expression.Call(
                resolvedServices,
                ServiceLookupHelpers.AddMethodInfo,
                keyExpression,
                resolvedVariable);

            Expression wrappedResult = WrapInValueTask(resolvedVariable);

            BlockExpression blockExpression = Expression.Block(
                typeof(ValueTask<object?>),
                new[]
                {
                    resolvedVariable
                },
                Expression.IfThen(
                    Expression.Not(tryGetValueExpression),
                    Expression.Block(
                        assignExpression,
                        addValueExpression)),
                wrappedResult);


            // The C# compiler would copy the lock object to guard against mutation.
            // We don't, since we know the lock object is readonly.
            ParameterExpression lockWasTaken = Expression.Variable(typeof(bool), "lockWasTaken");
            ParameterExpression sync = Sync;

            MethodCallExpression monitorEnter = Expression.Call(ServiceLookupHelpers.MonitorEnterMethodInfo, sync, lockWasTaken);
            MethodCallExpression monitorExit = Expression.Call(ServiceLookupHelpers.MonitorExitMethodInfo, sync);

            BlockExpression tryBody = Expression.Block(monitorEnter, blockExpression);
            ConditionalExpression finallyBody = Expression.IfThen(lockWasTaken, monitorExit);

            return Expression.Condition(
                    Expression.Property(
                        ScopeParameter,
                        typeof(ServiceProviderEngineScope)
                            .GetProperty(nameof(ServiceProviderEngineScope.IsRootScope), BindingFlags.Instance | BindingFlags.Public)!),
                    resolveRootScopeExpression,
                    Expression.Block(
                        typeof(ValueTask<object?>),
                        new[] { lockWasTaken },
                        Expression.TryFinally(tryBody, finallyBody))
                );
        }
    }
}
