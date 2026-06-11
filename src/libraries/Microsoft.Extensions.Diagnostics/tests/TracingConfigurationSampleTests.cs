// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Tracing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Extensions.Diagnostics.Tests
{
    public class TracingConfigurationSampleTests
    {
        private const string SampleListenerName = nameof(SampleActivityListener);

        [Fact]
        public void EnabledRuleAllowsActivityCreation()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnabledTracing:Default"] = "true",
                })
                .Build();

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddListener(SampleListenerName, SampleActivityListener.Configure)
                    .AddConfiguration(configuration))
                .Services
                .Configure<TracingOptions>(options =>
                    options.Rules.Add(new TracingRule("Demo.Source", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true)))
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.Source");

            AssertActivityCreation(source, "AllowedOperation", expectedCreated: true);
            AssertActivityCreation(source, "BlockedOperation", expectedCreated: true);
        }

        [Fact]
        public void DisabledRuleSkipsActivityCreation()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnabledTracing:Default"] = "true",
                })
                .Build();

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddListener(SampleListenerName, SampleActivityListener.Configure)
                    .AddConfiguration(configuration))
                .Services
                .Configure<TracingOptions>(options =>
                    options.Rules.Add(new TracingRule("Demo.Source", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false)))
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.Source");
            AssertActivityCreation(source, "BlockedOperation", expectedCreated: false);
        }

        [Fact]
        public void AddTracing_RegistersActivityListenerConfigurationFactory()
        {
            using var serviceProvider = new ServiceCollection()
                .AddTracing()
                .Services
                .BuildServiceProvider();

            var factory = serviceProvider.GetService<ActivityListenerConfigurationFactory>();
            Assert.NotNull(factory);
        }

        [Fact]
        public void AddConfiguration_MergesListenerConfigurationAcrossCalls()
        {
            const string listenerName = "SampleActivityListener";

            var configuration1 = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{listenerName}:EnabledTracing:SourceA"] = "true",
                    [$"{listenerName}:EnabledTracing:SourceB"] = "false",
                })
                .Build();

            var configuration2 = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{listenerName}:EnabledTracing:SourceB"] = "true",
                    [$"{listenerName}:EnabledTracing:SourceC"] = "false",
                })
                .Build();

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddConfiguration(configuration1)
                    .AddConfiguration(configuration2))
                .Services
                .BuildServiceProvider();

            var factory = serviceProvider.GetRequiredService<ActivityListenerConfigurationFactory>();
            IConfiguration mergedConfiguration = factory.GetConfiguration(listenerName);
            Assert.NotNull(mergedConfiguration);

            Assert.Equal("true", mergedConfiguration["EnabledTracing:SourceA"]);
            Assert.Equal("true", mergedConfiguration["EnabledTracing:SourceB"]);
            Assert.Equal("false", mergedConfiguration["EnabledTracing:SourceC"]);
        }

        [Fact]
        public void ScopeConfigurationMatchesSampleBehavior()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnabledTracing:Default"] = "false",
                    ["EnabledGlobalTracing:Demo.ScopeSource"] = "true",
                    ["EnabledLocalTracing:Demo.ScopeSource"] = "false",
                    ["EnabledLocalTracing:Demo.LocalOnlySource"] = "true",
                })
                .Build();

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddListener(SampleListenerName, SampleActivityListener.Configure)
                    .AddConfiguration(configuration))
                .Services
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();
            ActivitySourceFactory activitySourceFactory = serviceProvider.GetRequiredService<ActivitySourceFactory>();

            using var globalSource = new ActivitySource("Demo.ScopeSource");
            using var localScopeSource = activitySourceFactory.Create(new ActivitySourceOptions("Demo.ScopeSource"));
            using var localOnlySource = activitySourceFactory.Create("Demo.LocalOnlySource");
            using var blockedSource = new ActivitySource("Demo.BlockedSource");

            AssertActivityCreation(globalSource, "AllowedOperation", expectedCreated: true);
            AssertActivityCreation(localScopeSource, "AllowedOperation", expectedCreated: false);
            AssertActivityCreation(localOnlySource, "LocalOnlyOperation", expectedCreated: true);
            AssertActivityCreation(blockedSource, "AllowedOperation", expectedCreated: false);
        }

        [Fact]
        public void ExistingSourceRespondsToRuleChanges()
        {
            var optionsMonitor = new TestActivityOptionsMonitor(CreateOptions("Demo.ReloadableSource", enable: false));

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.ReloadableSource");

            AssertActivityCreation(source, "BeforeEnable", expectedCreated: false);

            optionsMonitor.Set(CreateOptions("Demo.ReloadableSource", enable: true));
            AssertActivityCreation(source, "AfterEnable", expectedCreated: true);

            optionsMonitor.Set(CreateOptions("Demo.ReloadableSource", enable: false));
            AssertActivityCreation(source, "AfterDisable", expectedCreated: false);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void UnspecifiedListenerNameRuleMatchesNamedListener(string? listenerName)
        {
            var optionsMonitor = new TestActivityOptionsMonitor(CreateOptions(
                new TracingRule("Demo.DefaultListenerBucket", operationName: null, listenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true)));

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.DefaultListenerBucket");
            AssertActivityCreation(source, "DefaultListenerBucketOperation", expectedCreated: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ExplicitListenerNameDisableWinsOverUnspecifiedEnable(string? unspecifiedListenerName)
        {
            var optionsMonitor = new TestActivityOptionsMonitor(CreateOptions(
                new TracingRule("Demo.ListenerSpecificDisable", operationName: null, listenerName: SampleListenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false),
                new TracingRule("Demo.ListenerSpecificDisable", operationName: null, listenerName: unspecifiedListenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true)));

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.ListenerSpecificDisable");
            AssertActivityCreation(source, "ListenerSpecificDisableOperation", expectedCreated: false);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ExplicitListenerNameEnableWinsOverUnspecifiedDisable(string? unspecifiedListenerName)
        {
            var optionsMonitor = new TestActivityOptionsMonitor(CreateOptions(
                new TracingRule("Demo.ListenerSpecificEnable", operationName: null, listenerName: SampleListenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true),
                new TracingRule("Demo.ListenerSpecificEnable", operationName: null, listenerName: unspecifiedListenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false)));

            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.ListenerSpecificEnable");
            AssertActivityCreation(source, "ListenerSpecificEnableOperation", expectedCreated: true);
        }

        [Fact]
        public void ActivitySourceFactoryCreate_WithInvalidScope_ThrowsTracingSpecificMessage()
        {
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .BuildServiceProvider();

            ActivitySourceFactory activitySourceFactory = serviceProvider.GetRequiredService<ActivitySourceFactory>();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                activitySourceFactory.Create(new ActivitySourceOptions("Demo.InvalidScopeSource")
                {
                    Scope = new object()
                }));

            Assert.Contains("activity source", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("meter", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SourceNamePatternWithMultipleWildcards_ThrowsTracingSpecificMessage()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>("sourceName", () =>
                new TracingRule("Demo*Wildcard*Source", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true));

            Assert.Contains("activity source", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("category", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FactoryConstruction_DisposesPartialRegistrations_WhenLaterListenerRegistrationThrows()
        {
            int firstListenerActivityStartedCalls = 0;

            var optionsMonitor = new TestActivityOptionsMonitor(CreateOptions(
                new TracingRule(sourceName: null, operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true)));

            // The second AddListener's configure callback throws after the first listener has been
            // fully registered. The factory ctor catches the throw, runs partial-construction cleanup
            // (disposes the first registration and its wrapper, detaching from s_allListeners /
            // s_activeSources), then rethrows.
            var thrown = new InvalidOperationException("boom");
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddListener("PartialCleanupFirstListener", b =>
                    {
                        b.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
                        b.ActivityStarted = _ => Interlocked.Increment(ref firstListenerActivityStartedCalls);
                    })
                    .AddListener("PartialCleanupBoomListener", _ => throw thrown))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(
                () => serviceProvider.GetRequiredService<ActivitySourceFactory>());
            Assert.Same(thrown, ex);

            // If the partial-construction cleanup ran, the wrapper attached for the first listener
            // has been detached from s_allListeners/s_activeSources. Creating and starting an
            // activity on a fresh ActivitySource must not invoke the wrapper's ActivityStarted
            // callback. If cleanup had not run, the rule (enable: true for any source) would let
            // the wrapper sample, and ActivityStarted would fire here.
            using (var freshSource = new ActivitySource("Demo.PartialCleanupRegression_" + Guid.NewGuid().ToString("N")))
            using (var activity = freshSource.StartActivity("op"))
            {
                Assert.Equal(0, Volatile.Read(ref firstListenerActivityStartedCalls));
            }
        }

        [Fact]
        public void ActivitySourceFactoryCreate_ReturnsDistinctSources_WhenTelemetrySchemaUrlDiffers()
        {
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .BuildServiceProvider();

            ActivitySourceFactory activitySourceFactory = serviceProvider.GetRequiredService<ActivitySourceFactory>();

            ActivitySource schemaV1 = activitySourceFactory.Create(new ActivitySourceOptions("Demo.SchemaCacheKey")
            {
                Version = "1.0",
                TelemetrySchemaUrl = "https://schema.test/v1",
            });
            ActivitySource schemaV2 = activitySourceFactory.Create(new ActivitySourceOptions("Demo.SchemaCacheKey")
            {
                Version = "1.0",
                TelemetrySchemaUrl = "https://schema.test/v2",
            });
            ActivitySource schemaV1Again = activitySourceFactory.Create(new ActivitySourceOptions("Demo.SchemaCacheKey")
            {
                Version = "1.0",
                TelemetrySchemaUrl = "https://schema.test/v1",
            });
            ActivitySource schemaNull = activitySourceFactory.Create(new ActivitySourceOptions("Demo.SchemaCacheKey")
            {
                Version = "1.0",
            });
            ActivitySource schemaNullAgain = activitySourceFactory.Create(new ActivitySourceOptions("Demo.SchemaCacheKey")
            {
                Version = "1.0",
            });

            Assert.NotSame(schemaV1, schemaV2);
            Assert.NotSame(schemaV1, schemaNull);
            Assert.NotSame(schemaV2, schemaNull);
            Assert.Same(schemaV1, schemaV1Again);
            Assert.Same(schemaNull, schemaNullAgain);

            Assert.Equal("https://schema.test/v1", schemaV1.TelemetrySchemaUrl);
            Assert.Equal("https://schema.test/v2", schemaV2.TelemetrySchemaUrl);
            Assert.Null(schemaNull.TelemetrySchemaUrl);
        }

        [Fact]
        public void ActivitySourceFactoryCreate_DoesNotMutateOptions_WhenActivitySourceCreationThrows()
        {
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .BuildServiceProvider();

            ActivitySourceFactory activitySourceFactory = serviceProvider.GetRequiredService<ActivitySourceFactory>();
            ThrowingTagsEnumerable originalTags = new ThrowingTagsEnumerable();
            ActivitySourceOptions options = new ActivitySourceOptions("Demo.ThrowingScopeSource")
            {
                Version = "1.0",
                Tags = originalTags,
                TelemetrySchemaUrl = "https://schema.test/v1",
            };

            Assert.Null(options.Scope);
            Assert.Throws<InvalidOperationException>(() => activitySourceFactory.Create(options));

            Assert.Equal("Demo.ThrowingScopeSource", options.Name);
            Assert.Equal("1.0", options.Version);
            Assert.Same(originalTags, options.Tags);
            Assert.Equal("https://schema.test/v1", options.TelemetrySchemaUrl);
            Assert.Null(options.Scope);
        }

        [Fact]
        public void FactoryDispose_ThrowsOnSubsequentCreate_AndDetachesWrapperListener()
        {
            const string SourcePrefix = "Demo.FactoryDispose.";

            int notifications = 0;

            ServiceProvider serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder
                    .AddListener("FactoryDisposeListener", b =>
                    {
                        b.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
                        b.ActivityStarted = _ => Interlocked.Increment(ref notifications);
                    })
                    .EnableTracing(SourcePrefix + "*"))
                .Services
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();
            ActivitySourceFactory factory = serviceProvider.GetRequiredService<ActivitySourceFactory>();

            using (ActivitySource before = new ActivitySource(SourcePrefix + "Before"))
            using (before.StartActivity("op"))
            {
            }
            Assert.Equal(1, Volatile.Read(ref notifications));

            serviceProvider.Dispose();

            Assert.Throws<ObjectDisposedException>(() => factory.Create(SourcePrefix + "AfterFactoryDispose"));

            int countAfterDispose = Volatile.Read(ref notifications);
            using (ActivitySource after = new ActivitySource(SourcePrefix + "After"))
            using (after.StartActivity("op"))
            {
            }
            Assert.Equal(countAfterDispose, Volatile.Read(ref notifications));
        }

        [Fact]
        public async Task FactoryAndOptionsReload_DoNotDeadlock_UnderConcurrentCreateAndReload()
        {
            const int Iterations = 200;
            const string SourcePrefix = "Demo.ConcurrentReload.";

            TracingRule enabled = new TracingRule(SourcePrefix + "*", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true);
            TracingRule disabled = new TracingRule(SourcePrefix + "*", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false);

            TestActivityOptionsMonitor optionsMonitor = new TestActivityOptionsMonitor(CreateOptions(enabled));

            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener("ConcurrentReloadListener", _ => { }))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();
            ActivitySourceFactory factory = serviceProvider.GetRequiredService<ActivitySourceFactory>();

            Task createTask = Task.Run(() =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    ActivitySource source = factory.Create(new ActivitySourceOptions(SourcePrefix + (i % 8)));
                    source.StartActivity("op")?.Dispose();
                }
            });

            Task reloadTask = Task.Run(() =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    optionsMonitor.Set(CreateOptions((i & 1) == 0 ? enabled : disabled));
                }
            });

            // Bounded wait: if either task deadlocks, Task.WhenAny lets us fail cleanly with a
            // diagnostic instead of letting xunit's outer infrastructure timeout swallow the cause.
            // Cancellation tokens would not help here because a deadlocked thread would not poll
            // them; the timeout has to fire on a separate scheduler.
            Task work = Task.WhenAll(createTask, reloadTask);
            Task completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.True(completed == work, "Concurrent Create/reload did not complete within 30s; likely deadlock.");

            // Surface any exception thrown by either task. Safe to await because WhenAny only
            // returned `work` when both inner tasks were complete.
            await work;
        }

        [Fact]
        public void OptionsChange_AfterFactoryDispose_IsNoOp()
        {
            const string SourcePrefix = "Demo.ReloadAfterDispose.";

            int activityStartedCalls = 0;

            TestActivityOptionsMonitor optionsMonitor = new TestActivityOptionsMonitor(
                CreateOptions(new TracingRule(SourcePrefix + "*", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false)));

            ServiceProvider serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener("ReloadAfterDisposeListener", b =>
                {
                    b.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
                    b.ActivityStarted = _ => Interlocked.Increment(ref activityStartedCalls);
                }))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            serviceProvider.Dispose();

            // After disposal, switch the rule to ENABLE the source. If the factory still observed
            // option changes (i.e. the OnChange registration was not disposed), it would re-evaluate
            // rules and the wrapper would either remain attached or re-attach. Either way an
            // activity started below would surface through the wrapper's ActivityStarted callback.
            // The factory's Dispose must disengage both the change-token subscription and the
            // wrapper, so this notification must be ignored.
            optionsMonitor.Set(CreateOptions(new TracingRule(SourcePrefix + "*", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true)));

            using (var source = new ActivitySource(SourcePrefix + "Post"))
            using (source.StartActivity("op"))
            {
            }

            Assert.Equal(0, Volatile.Read(ref activityStartedCalls));
        }

        [Fact]
        public void FactoryCtor_OptionsReloadDuringBootstrap_AppliesLatestSnapshot()
        {
            const string SourceName = "Demo.ReloadDuringBootstrap";

            TracingOptions initial = CreateOptions(SourceName, enable: false);
            TracingOptions afterSubscribe = CreateOptions(SourceName, enable: true);

            ReloadOnSubscribeMonitor optionsMonitor = new ReloadOnSubscribeMonitor(initial, afterSubscribe);

            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(SampleListenerName, SampleActivityListener.Configure))
                .Services
                .AddSingleton<IOptionsMonitor<TracingOptions>>(optionsMonitor)
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using ActivitySource source = new ActivitySource(SourceName);
            // The monitor silently advances CurrentValue from `initial` (source disabled) to
            // `afterSubscribe` (source enabled) when OnChange is invoked. Without the ctor's
            // post-subscribe reconciliation read, the factory would be stuck on the disabled
            // initial snapshot and the activity would not be created.
            AssertActivityCreation(source, "Op", expectedCreated: true);
        }

        private static TracingOptions CreateOptions(string sourceName, bool enable)
        {
            return CreateOptions(
                new TracingRule(sourceName, operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable),
                new TracingRule(sourceName, operationName: null, listenerName: SampleListenerName, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable));
        }

        private static TracingOptions CreateOptions(params TracingRule[] rules)
        {
            var options = new TracingOptions();
            foreach (TracingRule rule in rules)
            {
                options.Rules.Add(rule);
            }

            return options;
        }

        private static void AssertActivityCreation(ActivitySource source, string operationName, bool expectedCreated)
        {
            using Activity? activity = source.StartActivity(operationName);
            if (expectedCreated)
            {
                Assert.NotNull(activity);
            }
            else
            {
                Assert.Null(activity);
            }
        }

        [Fact]
        public void DisabledOperationName_FiltersNotifications_EvenWhenAnotherListenerCreatesActivity()
        {
            using var externalListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Demo.NotificationFilter",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(externalListener);

            var recording = new RecordingActivityListener();
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(nameof(RecordingActivityListener), recording.Configure))
                .Services
                .Configure<TracingOptions>(options =>
                {
                    options.Rules.Add(new TracingRule("Demo.NotificationFilter", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true));
                    options.Rules.Add(new TracingRule("Demo.NotificationFilter", operationName: "BlockedOperation", listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false));
                })
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.NotificationFilter");

            using (var allowed = source.StartActivity("AllowedOperation"))
            {
                Assert.NotNull(allowed);
            }
            using (var blocked = source.StartActivity("BlockedOperation"))
            {
                Assert.NotNull(blocked);
            }

            Assert.Equal(new[] { "AllowedOperation" }, recording.StartedNames);
            Assert.Equal(new[] { "AllowedOperation" }, recording.StoppedNames);
        }

        [Fact]
        public void EnabledOperationName_NotifiesListener_EvenWhenSourceDefaultDisabled()
        {
            var recording = new RecordingActivityListener();
            using var serviceProvider = new ServiceCollection()
                .AddTracing(builder => builder.AddListener(nameof(RecordingActivityListener), recording.Configure))
                .Services
                .Configure<TracingOptions>(options =>
                {
                    options.Rules.Add(new TracingRule("Demo.OptIn", operationName: null, listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: false));
                    options.Rules.Add(new TracingRule("Demo.OptIn", operationName: "OptedInOperation", listenerName: null, scopes: ActivitySourceScopes.Global | ActivitySourceScopes.Local, enable: true));
                })
                .BuildServiceProvider();

            serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            using var source = new ActivitySource("Demo.OptIn");

            using (var optedIn = source.StartActivity("OptedInOperation"))
            {
                Assert.NotNull(optedIn);
            }

            Assert.Equal(new[] { "OptedInOperation" }, recording.StartedNames);
            Assert.Equal(new[] { "OptedInOperation" }, recording.StoppedNames);
        }

        private sealed class RecordingActivityListener
        {
            private readonly object _lock = new();
            private readonly List<string> _started = new();
            private readonly List<string> _stopped = new();

            public void Configure(ActivityListenerBuilder builder)
            {
                builder.Sample = static (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded;
                builder.SampleUsingParentId = static (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllDataAndRecorded;
                builder.ActivityStarted = activity =>
                {
                    lock (_lock) { _started.Add(activity.OperationName); }
                };
                builder.ActivityStopped = activity =>
                {
                    lock (_lock) { _stopped.Add(activity.OperationName); }
                };
            }

            public IReadOnlyList<string> StartedNames
            {
                get { lock (_lock) { return _started.ToArray(); } }
            }

            public IReadOnlyList<string> StoppedNames
            {
                get { lock (_lock) { return _stopped.ToArray(); } }
            }
        }

        private static class SampleActivityListener
        {
            public static void Configure(ActivityListenerBuilder builder)
            {
                builder.Sample = static (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded;
                builder.SampleUsingParentId = static (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllDataAndRecorded;
            }
        }

        private sealed class ThrowingTagsEnumerable : IEnumerable<KeyValuePair<string, object?>>
        {
            public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => new ThrowingTagsEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            private sealed class ThrowingTagsEnumerator : IEnumerator<KeyValuePair<string, object?>>
            {
                public KeyValuePair<string, object?> Current => default;

                object IEnumerator.Current => Current;

                public bool MoveNext() => throw new InvalidOperationException("Test exception.");

                public void Dispose()
                {
                }

                public void Reset() => throw new NotSupportedException();
            }
        }

        private sealed class ReloadOnSubscribeMonitor : IOptionsMonitor<TracingOptions>
        {
            private readonly TracingOptions _afterSubscribe;
            private TracingOptions _current;
            private bool _flipped;

            public ReloadOnSubscribeMonitor(TracingOptions initial, TracingOptions afterSubscribe)
            {
                _current = initial;
                _afterSubscribe = afterSubscribe;
            }

            public TracingOptions CurrentValue => Volatile.Read(ref _current);

            public TracingOptions Get(string? name) => CurrentValue;

            public IDisposable OnChange(Action<TracingOptions, string?> listener)
            {
                // Simulate a configuration reload that committed silently between the consumer's
                // initial CurrentValue read and this subscription. The new snapshot is NOT pushed
                // through the listener (that's the whole point of the race we are exercising —
                // the change was committed before the listener was attached).
                if (!_flipped)
                {
                    _flipped = true;
                    Volatile.Write(ref _current, _afterSubscribe);
                }

                return new NoopDisposable();
            }

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        private sealed class TestActivityOptionsMonitor : IOptionsMonitor<TracingOptions>
        {
            private readonly List<Action<TracingOptions, string?>> _callbacks = new();

            public TestActivityOptionsMonitor(TracingOptions currentValue)
            {
                CurrentValue = currentValue;
            }

            public TracingOptions CurrentValue { get; private set; }

            public TracingOptions Get(string? name) => CurrentValue;

            public IDisposable OnChange(Action<TracingOptions, string?> listener)
            {
                _callbacks.Add(listener);
                return new CallbackRegistration(_callbacks, listener);
            }

            public void Set(TracingOptions options)
            {
                CurrentValue = options;
                foreach (Action<TracingOptions, string?> callback in _callbacks.ToArray())
                {
                    callback(options, Options.Options.DefaultName);
                }
            }

            private sealed class CallbackRegistration : IDisposable
            {
                private readonly List<Action<TracingOptions, string?>> _callbacks;
                private readonly Action<TracingOptions, string?> _callback;

                public CallbackRegistration(List<Action<TracingOptions, string?>> callbacks, Action<TracingOptions, string?> callback)
                {
                    _callbacks = callbacks;
                    _callback = callback;
                }

                public void Dispose()
                {
                    _callbacks.Remove(_callback);
                }
            }
        }
    }
}
