// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    [RequiresDynamicCode("Creates DynamicMethods")]
    internal sealed class ILEmitResolverBuilder : CallSiteVisitor<ILEmitResolverBuilderContext, object?>
    {
        private static readonly MethodInfo ResolvedServicesGetter = typeof(ServiceProviderEngineScope).GetProperty(
            nameof(ServiceProviderEngineScope.ResolvedServices), BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!;

        private static readonly MethodInfo ScopeLockGetter = typeof(ServiceProviderEngineScope).GetProperty(
            nameof(ServiceProviderEngineScope.Sync), BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!;

        private static readonly MethodInfo ScopeIsRootScope = typeof(ServiceProviderEngineScope).GetProperty(
            nameof(ServiceProviderEngineScope.IsRootScope), BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;

        private static readonly MethodInfo CallSiteRuntimeResolverResolveAsyncMethod = typeof(CallSiteRuntimeResolver).GetMethod(
            nameof(CallSiteRuntimeResolver.ResolveAsync), BindingFlags.Public | BindingFlags.Instance)!;

        private static readonly MethodInfo CallSiteRuntimeResolverInstanceField = typeof(CallSiteRuntimeResolver).GetProperty(
            nameof(CallSiteRuntimeResolver.Instance), BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;

        private static readonly FieldInfo FactoriesField = typeof(ILEmitResolverBuilderRuntimeContext).GetField(nameof(ILEmitResolverBuilderRuntimeContext.Factories))!;
        private static readonly FieldInfo ConstantsField = typeof(ILEmitResolverBuilderRuntimeContext).GetField(nameof(ILEmitResolverBuilderRuntimeContext.Constants))!;
        private static readonly MethodInfo GetTypeFromHandleMethod = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!;

        private static readonly ConstructorInfo CacheKeyCtor = typeof(ServiceCacheKey).GetConstructors()[0];

        // ValueTask<object?> reflection
        private static readonly ConstructorInfo ValueTaskObjectCtor =
            typeof(ValueTask<object?>).GetConstructor(new[] { typeof(object) })!;

        private static readonly MethodInfo ValueTaskIsCompletedSuccessfullyGetter =
            typeof(ValueTask<object?>).GetProperty(nameof(ValueTask<object?>.IsCompletedSuccessfully))!.GetMethod!;

        private static readonly MethodInfo ValueTaskResultGetter =
            typeof(ValueTask<object?>).GetProperty(nameof(ValueTask<object?>.Result))!.GetMethod!;

        private static readonly MethodInfo GetSynchronousResultObjectMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.GetSynchronousResult))!
                .MakeGenericMethod(typeof(object));

        private static readonly MethodInfo AwaitConstructorMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitConstructor), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo AwaitArrayElementsMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitArrayElements), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo AwaitAndCaptureDisposableMethod =
            typeof(ValueTaskHelpers).GetMethod(nameof(ValueTaskHelpers.AwaitAndCaptureDisposable), BindingFlags.NonPublic | BindingFlags.Static)!;

        private sealed class ILEmitResolverBuilderRuntimeContext
        {
            public object?[]? Constants;
            public Func<IServiceProvider, object>[]? Factories;
        }

        private struct GeneratedMethod
        {
            public Func<ServiceProviderEngineScope, ValueTask<object?>> Lambda;

            public ILEmitResolverBuilderRuntimeContext Context;
            public DynamicMethod DynamicMethod;
        }

        private readonly ServiceProviderEngineScope _rootScope;

        private readonly ConcurrentDictionary<ServiceCacheKey, GeneratedMethod> _scopeResolverCache;

        private readonly Func<ServiceCacheKey, ServiceCallSite, GeneratedMethod> _buildTypeDelegate;

        public ILEmitResolverBuilder(ServiceProvider serviceProvider)
        {
            _rootScope = serviceProvider.Root;
            _scopeResolverCache = new ConcurrentDictionary<ServiceCacheKey, GeneratedMethod>();
            _buildTypeDelegate = (key, cs) => BuildTypeNoCache(cs);
        }

        public Func<ServiceProviderEngineScope, ValueTask<object?>> Build(ServiceCallSite callSite)
        {
            return BuildType(callSite).Lambda;
        }

        private GeneratedMethod BuildType(ServiceCallSite callSite)
        {
            // Only scope methods are cached
            if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
            {
#if NETFRAMEWORK || NETSTANDARD2_0
                return _scopeResolverCache.GetOrAdd(callSite.Cache.Key, key => _buildTypeDelegate(key, callSite));
#else
                return _scopeResolverCache.GetOrAdd(callSite.Cache.Key, _buildTypeDelegate, callSite);
#endif
            }

            return BuildTypeNoCache(callSite);
        }

        private GeneratedMethod BuildTypeNoCache(ServiceCallSite callSite)
        {
            // We need to skip visibility checks because services/constructors might be private
            var dynamicMethod = new DynamicMethod("ResolveService",
                attributes: MethodAttributes.Public | MethodAttributes.Static,
                callingConvention: CallingConventions.Standard,
                returnType: typeof(ValueTask<object?>),
                parameterTypes: new[] { typeof(ILEmitResolverBuilderRuntimeContext), typeof(ServiceProviderEngineScope) },
                owner: GetType(),
                skipVisibility: true);

            // In traces we've seen methods range from 100B - 4K sized methods since we've
            // stop trying to inline everything into scoped methods. We'll pay for a couple of resizes
            // so there'll be allocations but we could potentially change ILGenerator to use the array pool
            ILGenerator ilGenerator = dynamicMethod.GetILGenerator(512);
            ILEmitResolverBuilderRuntimeContext runtimeContext = GenerateMethodBody(callSite, ilGenerator);

#if SAVE_ASSEMBLIES
            var assemblyName = "Test" + DateTime.Now.Ticks;
            var fileName = assemblyName + ".dll";

#if NETFRAMEWORK
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndSave);
            var module = assembly.DefineDynamicModule(assemblyName, fileName);
#else
            var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
            var module = assembly.DefineDynamicModule(assemblyName);
#endif
            var type = module.DefineType(callSite.ServiceType.Name + "Resolver");

            var method = type.DefineMethod(
                "ResolveService", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard, typeof(ValueTask<object?>),
                new[] { typeof(ILEmitResolverBuilderRuntimeContext), typeof(ServiceProviderEngineScope) });

            GenerateMethodBody(callSite, method.GetILGenerator());
            type.CreateTypeInfo();
            assembly.Save(fileName);
#endif
            DependencyInjectionEventSource.Log.DynamicMethodBuilt(_rootScope.RootProvider, callSite.ServiceType, ilGenerator.ILOffset);

            return new GeneratedMethod()
            {
                Lambda = (Func<ServiceProviderEngineScope, ValueTask<object?>>)dynamicMethod.CreateDelegate(typeof(Func<ServiceProviderEngineScope, ValueTask<object?>>), runtimeContext),
                Context = runtimeContext,
                DynamicMethod = dynamicMethod
            };
        }


        protected override object? VisitDisposeCache(ServiceCallSite transientCallSite, ILEmitResolverBuilderContext argument)
        {
            ILGenerator il = argument.Generator;

            VisitCallSiteMain(transientCallSite, argument);
            // Stack: [ValueTask<object?>]

            if (!transientCallSite.CaptureDisposable)
            {
                return null;
            }

            LocalBuilder vtLocal = il.DeclareLocal(typeof(ValueTask<object?>));
            il.Emit(OpCodes.Stloc, vtLocal);

            Label slowPath = il.DefineLabel();
            Label returnLabel = il.DefineLabel();

            il.Emit(OpCodes.Ldloca, vtLocal);
            il.Emit(OpCodes.Call, ValueTaskIsCompletedSuccessfullyGetter);
            il.Emit(OpCodes.Brfalse, slowPath);

            // Fast path: scope.CaptureDisposable(vt.Result), wrap in ValueTask
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca, vtLocal);
            il.Emit(OpCodes.Call, ValueTaskResultGetter);
            il.Emit(OpCodes.Callvirt, ServiceLookupHelpers.CaptureDisposableMethodInfo);
            il.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            il.Emit(OpCodes.Br, returnLabel);

            // Slow path: AwaitAndCaptureDisposable(vt, scope)
            il.MarkLabel(slowPath);
            il.Emit(OpCodes.Ldloc, vtLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, AwaitAndCaptureDisposableMethod);

            il.MarkLabel(returnLabel);
            // Stack: [ValueTask<object?>]
            return null;
        }

        protected override object? VisitConstructor(ConstructorCallSite constructorCallSite, ILEmitResolverBuilderContext argument)
        {
            ILGenerator il = argument.Generator;
            ServiceCallSite[] paramCallSites = constructorCallSite.ParameterCallSites;

            if (paramCallSites.Length == 0)
            {
                il.Emit(OpCodes.Newobj, constructorCallSite.ConstructorInfo);
                if (constructorCallSite.ImplementationType!.IsValueType)
                {
                    il.Emit(OpCodes.Box, constructorCallSite.ImplementationType);
                }
                il.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
                return null;
            }

            // Declare locals for each parameter's ValueTask
            LocalBuilder[] vtLocals = new LocalBuilder[paramCallSites.Length];
            for (int i = 0; i < paramCallSites.Length; i++)
            {
                vtLocals[i] = il.DeclareLocal(typeof(ValueTask<object?>));
            }

            // Resolve each parameter → ValueTask<object?>, store in local
            for (int i = 0; i < paramCallSites.Length; i++)
            {
                VisitCallSite(paramCallSites[i], argument);
                il.Emit(OpCodes.Stloc, vtLocals[i]);
            }

            Label slowPath = il.DefineLabel();
            Label returnLabel = il.DefineLabel();

            // Check all IsCompletedSuccessfully
            for (int i = 0; i < paramCallSites.Length; i++)
            {
                il.Emit(OpCodes.Ldloca, vtLocals[i]);
                il.Emit(OpCodes.Call, ValueTaskIsCompletedSuccessfullyGetter);
                il.Emit(OpCodes.Brfalse, slowPath);
            }

            // Fast path: extract .Result, cast, construct, wrap in ValueTask
            for (int i = 0; i < paramCallSites.Length; i++)
            {
                il.Emit(OpCodes.Ldloca, vtLocals[i]);
                il.Emit(OpCodes.Call, ValueTaskResultGetter);
                if (paramCallSites[i].ServiceType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, paramCallSites[i].ServiceType);
                }
            }
            il.Emit(OpCodes.Newobj, constructorCallSite.ConstructorInfo);
            if (constructorCallSite.ImplementationType!.IsValueType)
            {
                il.Emit(OpCodes.Box, constructorCallSite.ImplementationType);
            }
            il.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            il.Emit(OpCodes.Br, returnLabel);

            // Slow path: create ValueTask<object?>[] array, call AwaitConstructor
            il.MarkLabel(slowPath);
            AddConstant(argument, constructorCallSite.ConstructorInfo);
            il.Emit(OpCodes.Castclass, typeof(ConstructorInfo));
            il.Emit(OpCodes.Ldc_I4, paramCallSites.Length);
            il.Emit(OpCodes.Newarr, typeof(ValueTask<object?>));
            for (int i = 0; i < paramCallSites.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, vtLocals[i]);
                il.Emit(OpCodes.Stelem, typeof(ValueTask<object?>));
            }
            il.Emit(OpCodes.Call, AwaitConstructorMethod);

            il.MarkLabel(returnLabel);
            // Stack: [ValueTask<object?>]
            return null;
        }

        protected override object? VisitRootCache(ServiceCallSite callSite, ILEmitResolverBuilderContext argument)
        {
            AddConstant(argument, CallSiteRuntimeResolver.Instance.Resolve(callSite, _rootScope));
            argument.Generator.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            return null;
        }

        protected override object? VisitScopeCache(ServiceCallSite scopedCallSite, ILEmitResolverBuilderContext argument)
        {
            GeneratedMethod generatedMethod = BuildType(scopedCallSite);

            // Type builder doesn't support invoking dynamic methods, replace them with delegate.Invoke calls
#if SAVE_ASSEMBLIES
            AddConstant(argument, generatedMethod.Lambda);
            // ProviderScope
            argument.Generator.Emit(OpCodes.Ldarg_1);
            argument.Generator.Emit(OpCodes.Call, generatedMethod.Lambda.GetType().GetMethod("Invoke")!);
#else
            AddConstant(argument, generatedMethod.Context);
            // ProviderScope
            argument.Generator.Emit(OpCodes.Ldarg_1);
            argument.Generator.Emit(OpCodes.Call, generatedMethod.DynamicMethod);
#endif
            // Stack: [ValueTask<object?>]
            return null;
        }

        protected override object? VisitConstant(ConstantCallSite constantCallSite, ILEmitResolverBuilderContext argument)
        {
            AddConstant(argument, constantCallSite.DefaultValue);
            argument.Generator.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            return null;
        }

        protected override object? VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, ILEmitResolverBuilderContext argument)
        {
            // [return] ProviderScope
            argument.Generator.Emit(OpCodes.Ldarg_1);
            argument.Generator.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            return null;
        }

        protected override object? VisitIEnumerable(IEnumerableCallSite enumerableCallSite, ILEmitResolverBuilderContext argument)
        {
            ILGenerator il = argument.Generator;

            if (enumerableCallSite.ServiceCallSites.Length == 0)
            {
                il.Emit(OpCodes.Call, ServiceLookupHelpers.GetArrayEmptyMethodInfo(enumerableCallSite.ItemType));
                il.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
                return null;
            }

            int count = enumerableCallSite.ServiceCallSites.Length;

            // Declare locals for each element's ValueTask
            LocalBuilder[] vtLocals = new LocalBuilder[count];
            for (int i = 0; i < count; i++)
            {
                vtLocals[i] = il.DeclareLocal(typeof(ValueTask<object?>));
            }

            // Resolve each element → ValueTask<object?>, store in local
            for (int i = 0; i < count; i++)
            {
                VisitCallSite(enumerableCallSite.ServiceCallSites[i], argument);
                il.Emit(OpCodes.Stloc, vtLocals[i]);
            }

            Label slowPath = il.DefineLabel();
            Label returnLabel = il.DefineLabel();

            // Check all IsCompletedSuccessfully
            for (int i = 0; i < count; i++)
            {
                il.Emit(OpCodes.Ldloca, vtLocals[i]);
                il.Emit(OpCodes.Call, ValueTaskIsCompletedSuccessfullyGetter);
                il.Emit(OpCodes.Brfalse, slowPath);
            }

            // Fast path: create typed array with extracted results, wrap in ValueTask
            il.Emit(OpCodes.Ldc_I4, count);
            il.Emit(OpCodes.Newarr, enumerableCallSite.ItemType);
            for (int i = 0; i < count; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloca, vtLocals[i]);
                il.Emit(OpCodes.Call, ValueTaskResultGetter);
                if (enumerableCallSite.ServiceCallSites[i].ServiceType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, enumerableCallSite.ServiceCallSites[i].ServiceType);
                }
                il.Emit(OpCodes.Stelem, enumerableCallSite.ItemType);
            }
            il.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
            il.Emit(OpCodes.Br, returnLabel);

            // Slow path: create ValueTask array, call AwaitArrayElements
            il.MarkLabel(slowPath);
            AddConstant(argument, enumerableCallSite.ItemType);
            il.Emit(OpCodes.Castclass, typeof(Type));
            il.Emit(OpCodes.Ldc_I4, count);
            il.Emit(OpCodes.Newarr, typeof(ValueTask<object?>));
            for (int i = 0; i < count; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, vtLocals[i]);
                il.Emit(OpCodes.Stelem, typeof(ValueTask<object?>));
            }
            il.Emit(OpCodes.Call, AwaitArrayElementsMethod);

            il.MarkLabel(returnLabel);
            // Stack: [ValueTask<object?>]
            return null;
        }

        protected override object? VisitFactory(FactoryCallSite factoryCallSite, ILEmitResolverBuilderContext argument)
        {
            argument.Factories ??= new List<Func<IServiceProvider, object>>();

            // this.Factories[i](ProviderScope)
            argument.Generator.Emit(OpCodes.Ldarg_0);
            argument.Generator.Emit(OpCodes.Ldfld, FactoriesField);

            argument.Generator.Emit(OpCodes.Ldc_I4, argument.Factories.Count);
            argument.Generator.Emit(OpCodes.Ldelem, typeof(Func<IServiceProvider, object>));

            argument.Generator.Emit(OpCodes.Ldarg_1);
            argument.Generator.Emit(OpCodes.Call, ServiceLookupHelpers.InvokeFactoryMethodInfo);
            argument.Generator.Emit(OpCodes.Newobj, ValueTaskObjectCtor);

            argument.Factories.Add(factoryCallSite.Factory);
            return null;
        }

        private static void AddConstant(ILEmitResolverBuilderContext argument, object? value)
        {
            argument.Constants ??= new List<object?>();

            // this.Constants[i]
            argument.Generator.Emit(OpCodes.Ldarg_0);
            argument.Generator.Emit(OpCodes.Ldfld, ConstantsField);

            argument.Generator.Emit(OpCodes.Ldc_I4, argument.Constants.Count);
            argument.Generator.Emit(OpCodes.Ldelem, typeof(object));
            argument.Constants.Add(value);
        }

        private static void AddCacheKey(ILEmitResolverBuilderContext argument, ServiceCacheKey key)
        {
            var id = key.ServiceIdentifier;

            // new ServiceCacheKey(key.ServiceKey, key.type, key.slot)
            AddConstant(argument, id.ServiceKey);
            argument.Generator.Emit(OpCodes.Ldtoken, id.ServiceType);
            argument.Generator.Emit(OpCodes.Call, GetTypeFromHandleMethod);
            argument.Generator.Emit(OpCodes.Ldc_I4, key.Slot);
            argument.Generator.Emit(OpCodes.Newobj, CacheKeyCtor);
        }

        private ILEmitResolverBuilderRuntimeContext GenerateMethodBody(ServiceCallSite callSite, ILGenerator generator)
        {
            var context = new ILEmitResolverBuilderContext(generator)
            {
                Constants = null,
                Factories = null
            };

            // if (scope.IsRootScope)
            // {
            //    return CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope);
            // }
            // var cacheKey = scopedCallSite.CacheKey;
            // object sync;
            // bool lockTaken;
            // object result;
            // try
            // {
            //    var resolvedServices = scope.ResolvedServices;
            //    sync = scope.Sync;
            //    Monitor.Enter(sync, ref lockTaken);
            //    if (!resolvedServices.TryGetValue(cacheKey, out result)
            //    {
            //       ValueTask<object?> vt = [createvalue];
            //       result = ValueTaskHelpers.GetSynchronousResult(vt);
            //       CaptureDisposable(result);
            //       resolvedServices.Add(cacheKey, result);
            //    }
            // }
            // finally
            // {
            //   if (lockTaken)
            //   {
            //      Monitor.Exit(sync);
            //   }
            // }
            // return new ValueTask<object?>(result);

            if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
            {
                LocalBuilder cacheKeyLocal = context.Generator.DeclareLocal(typeof(ServiceCacheKey));
                LocalBuilder resolvedServicesLocal = context.Generator.DeclareLocal(typeof(IDictionary<ServiceCacheKey, object>));
                LocalBuilder syncLocal = context.Generator.DeclareLocal(typeof(object));
                LocalBuilder lockTakenLocal = context.Generator.DeclareLocal(typeof(bool));
                LocalBuilder resultLocal = context.Generator.DeclareLocal(typeof(object));

                Label skipCreationLabel = context.Generator.DefineLabel();
                Label returnLabel = context.Generator.DefineLabel();
                Label defaultLabel = context.Generator.DefineLabel();

                // Check if scope IsRootScope
                context.Generator.Emit(OpCodes.Ldarg_1);
                context.Generator.Emit(OpCodes.Callvirt, ScopeIsRootScope);
                context.Generator.Emit(OpCodes.Brfalse_S, defaultLabel);

                // Root scope: return RuntimeResolver.Instance.ResolveAsync(callSite, scope)
                context.Generator.Emit(OpCodes.Call, CallSiteRuntimeResolverInstanceField);
                AddConstant(context, callSite);
                context.Generator.Emit(OpCodes.Ldarg_1);
                context.Generator.Emit(OpCodes.Callvirt, CallSiteRuntimeResolverResolveAsyncMethod);
                context.Generator.Emit(OpCodes.Ret);

                // Generate cache key
                context.Generator.MarkLabel(defaultLabel);
                AddCacheKey(context, callSite.Cache.Key);
                // and store to local
                context.Generator.Emit(OpCodes.Stloc, cacheKeyLocal);

                context.Generator.BeginExceptionBlock();

                // scope
                context.Generator.Emit(OpCodes.Ldarg_1);
                // .ResolvedServices
                context.Generator.Emit(OpCodes.Callvirt, ResolvedServicesGetter);
                // Store resolved services
                context.Generator.Emit(OpCodes.Stloc, resolvedServicesLocal);

                // scope
                context.Generator.Emit(OpCodes.Ldarg_1);
                // .Sync
                context.Generator.Emit(OpCodes.Callvirt, ScopeLockGetter);
                // Store syncLocal
                context.Generator.Emit(OpCodes.Stloc, syncLocal);

                // Load syncLocal
                context.Generator.Emit(OpCodes.Ldloc, syncLocal);
                // Load address of lockTaken
                context.Generator.Emit(OpCodes.Ldloca, lockTakenLocal);
                // Monitor.Enter
                context.Generator.Emit(OpCodes.Call, ServiceLookupHelpers.MonitorEnterMethodInfo);

                // Load resolved services
                context.Generator.Emit(OpCodes.Ldloc, resolvedServicesLocal);
                // Load cache key
                context.Generator.Emit(OpCodes.Ldloc, cacheKeyLocal);
                // Load address of result local
                context.Generator.Emit(OpCodes.Ldloca, resultLocal);
                // .TryGetValue
                context.Generator.Emit(OpCodes.Callvirt, ServiceLookupHelpers.TryGetValueMethodInfo);

                // Jump to the end if already in cache
                context.Generator.Emit(OpCodes.Brtrue, skipCreationLabel);

                // Create value — VisitCallSiteMain pushes ValueTask<object?> on stack
                VisitCallSiteMain(callSite, context);
                // Unwrap synchronously: GetSynchronousResult(vt) → object?
                context.Generator.Emit(OpCodes.Call, GetSynchronousResultObjectMethod);
                context.Generator.Emit(OpCodes.Stloc, resultLocal);

                if (callSite.CaptureDisposable)
                {
                    BeginCaptureDisposable(context);
                    context.Generator.Emit(OpCodes.Ldloc, resultLocal);
                    EndCaptureDisposable(context);
                    // Pop value returned by CaptureDisposable off the stack
                    generator.Emit(OpCodes.Pop);
                }

                // load resolvedServices
                context.Generator.Emit(OpCodes.Ldloc, resolvedServicesLocal);
                // load cache key
                context.Generator.Emit(OpCodes.Ldloc, cacheKeyLocal);
                // load value
                context.Generator.Emit(OpCodes.Ldloc, resultLocal);
                // .Add
                context.Generator.Emit(OpCodes.Callvirt, ServiceLookupHelpers.AddMethodInfo);

                context.Generator.MarkLabel(skipCreationLabel);

                context.Generator.BeginFinallyBlock();

                // load lockTaken
                context.Generator.Emit(OpCodes.Ldloc, lockTakenLocal);
                // return if not
                context.Generator.Emit(OpCodes.Brfalse, returnLabel);
                // Load syncLocal
                context.Generator.Emit(OpCodes.Ldloc, syncLocal);
                // Monitor.Exit
                context.Generator.Emit(OpCodes.Call, ServiceLookupHelpers.MonitorExitMethodInfo);

                context.Generator.MarkLabel(returnLabel);

                context.Generator.EndExceptionBlock();

                // load value and wrap in ValueTask
                context.Generator.Emit(OpCodes.Ldloc, resultLocal);
                context.Generator.Emit(OpCodes.Newobj, ValueTaskObjectCtor);
                // return
                context.Generator.Emit(OpCodes.Ret);
            }
            else
            {
                VisitCallSite(callSite, context);
                // Stack: [ValueTask<object?>]
                // return
                context.Generator.Emit(OpCodes.Ret);
            }

            return new ILEmitResolverBuilderRuntimeContext
            {
                Constants = context.Constants?.ToArray(),
                Factories = context.Factories?.ToArray()
            };
        }

        private static void BeginCaptureDisposable(ILEmitResolverBuilderContext argument)
        {
            argument.Generator.Emit(OpCodes.Ldarg_1);
        }

        private static void EndCaptureDisposable(ILEmitResolverBuilderContext argument)
        {
            // When calling CaptureDisposable we expect callee and arguments to be on the stack
            argument.Generator.Emit(OpCodes.Callvirt, ServiceLookupHelpers.CaptureDisposableMethodInfo);
        }
    }
}
