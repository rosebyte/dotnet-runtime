// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Microsoft.Extensions.Configuration.Test
{
    public class ConfigurationTest
    {
        [Theory]
        [InlineData(ConfigurationFailureStage.Build, false)]
        [InlineData(ConfigurationFailureStage.Build, true)]
        [InlineData(ConfigurationFailureStage.Load, false)]
        [InlineData(ConfigurationFailureStage.Load, true)]
        [InlineData(ConfigurationFailureStage.GetReloadToken, false)]
        [InlineData(ConfigurationFailureStage.GetReloadToken, true)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback, false)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback, true)]
        public void FailedBuildDisposesOwnedCandidates(ConfigurationFailureStage stage, bool cleanupThrows)
        {
            var first = new OwnedConfigurationFailureSource();
            var last = new OwnedConfigurationFailureSource();
            using var borrowedProvider = new ConfigurationFailureProvider();
            var failingSource = new ConfigurationFailureSource(borrowedProvider);
            Exception failure = failingSource.SetFailure(stage);
            var cleanupFailure = new InvalidOperationException("Owned provider disposal failed.");
            first.Configure = p => p.DisposeException = cleanupThrows ? cleanupFailure : null;
            var builder = new ConfigurationBuilder().Add(first).Add(failingSource).Add(last);

            if (cleanupThrows)
            {
                AggregateException exception = Assert.Throws<AggregateException>(() => builder.Build());
                Assert.Equal(new[] { failure, cleanupFailure }, exception.Flatten().InnerExceptions);
            }
            else
            {
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Build()));
            }

            Assert.Equal(1, first.Provider.DisposeCount);
            Assert.Equal(0, first.Provider.Token.RegistrationCount);
            Assert.Equal(0, borrowedProvider.DisposeCount);
            if (stage != ConfigurationFailureStage.Build)
            {
                Assert.Equal(1, last.Provider.DisposeCount);
            }
        }

        [Fact]
        public void FailedBuildWithSourceMutationDoesNotMisidentifyOwnedCandidates()
        {
            using var cached = new ConfigurationFailureProvider();
            using var liveRoot = new ConfigurationRoot(new[] { cached });
            var source = new ConfigurationFailureSource(cached)
            {
                BeforeBuild = builder => builder.Sources.Insert(0, new OwnedConfigurationFailureSource())
            };
            var builder = new ConfigurationBuilder().Add(source);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Equal(0, cached.DisposeCount);
            Assert.Equal(1, cached.Token.RegistrationCount);
            Assert.Equal("value", liveRoot["key"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedBuildDoesNotDisposeExistingChainedConfiguration(bool shouldDispose)
        {
            using var provider = new ConfigurationFailureProvider();
            using var configuration = new ConfigurationRoot(new[] { provider });
            using var failing = new ConfigurationFailureProvider();
            var failingSource = new ConfigurationFailureSource(failing);
            Exception failure = failingSource.SetFailure(ConfigurationFailureStage.Load);
            var builder = new ConfigurationBuilder()
                .AddConfiguration(configuration, shouldDisposeConfiguration: shouldDispose)
                .Add(failingSource);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Build()));
            Assert.Equal(0, provider.DisposeCount);
            Assert.Equal(1, provider.Token.RegistrationCount);
            Assert.Equal("value", configuration["key"]);
        }

        [Theory]
        [InlineData(false, ConfigurationFailureStage.Load)]
        [InlineData(false, ConfigurationFailureStage.GetReloadToken)]
        [InlineData(false, ConfigurationFailureStage.RegisterChangeCallback)]
        [InlineData(true, ConfigurationFailureStage.Build)]
        [InlineData(true, ConfigurationFailureStage.Load)]
        [InlineData(true, ConfigurationFailureStage.GetReloadToken)]
        [InlineData(true, ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedConstructionPreservesProvidersAndReleasesRegistrations(bool useBuilder, ConfigurationFailureStage stage)
        {
            using var first = new ConfigurationFailureProvider();
            using var second = new ConfigurationFailureProvider();
            using var last = new ConfigurationFailureProvider();
            var failingSource = new ConfigurationFailureSource(second);
            Exception failure = failingSource.SetFailure(stage);
            var builder = new ConfigurationBuilder()
                .Add(new ConfigurationFailureSource(first))
                .Add(failingSource)
                .Add(new ConfigurationFailureSource(last));

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                useBuilder ? builder.Build() : new ConfigurationRoot(new[] { first, second, last })));

            foreach (ConfigurationFailureProvider provider in new[] { first, second, last })
            {
                Assert.Equal(0, provider.Token.RegistrationCount);
                Assert.Equal(0, provider.DisposeCount);
            }

            failingSource.ClearFailure();
            using var root = (ConfigurationRoot)builder.Build();
            Assert.Equal(1, first.Token.RegistrationCount);
            Assert.Equal(1, second.Token.RegistrationCount);
            Assert.Equal(1, last.Token.RegistrationCount);
        }

        [Theory]
        [InlineData(ConfigurationFailureStage.Build)]
        [InlineData(ConfigurationFailureStage.Load)]
        [InlineData(ConfigurationFailureStage.GetReloadToken)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedBuildPreservesCachedProviderInAnotherRoot(ConfigurationFailureStage stage)
        {
            using var cached = new ConfigurationFailureProvider();
            using var failing = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(cached);
            var builder = new ConfigurationBuilder().Add(source);
            using var liveRoot = (ConfigurationRoot)builder.Build();
            var failingSource = new ConfigurationFailureSource(failing);
            Exception failure = failingSource.SetFailure(stage);
            builder.Add(failingSource);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Build()));
            Assert.Equal(0, cached.DisposeCount);
            Assert.Equal(1, cached.Token.RegistrationCount);
            Assert.Equal(0, failing.Token.RegistrationCount);
            Assert.Equal("value", liveRoot["key"]);

            IChangeToken token = liveRoot.GetReloadToken();
            cached.Reload();
            Assert.True(token.HasChanged);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedConstructionAttemptsAllRegistrationCleanup(bool useBuilder)
        {
            using var first = new ConfigurationFailureProvider();
            using var second = new ConfigurationFailureProvider();
            using var failing = new ConfigurationFailureProvider();
            var disposalFailure = new InvalidOperationException("Registration disposal failed.");
            first.Token.DisposeException = disposalFailure;
            var failingSource = new ConfigurationFailureSource(failing);
            Exception failure = failingSource.SetFailure(ConfigurationFailureStage.Load);
            var builder = new ConfigurationBuilder()
                .Add(new ConfigurationFailureSource(first))
                .Add(new ConfigurationFailureSource(second))
                .Add(failingSource);

            AggregateException exception = Assert.Throws<AggregateException>(() =>
                useBuilder ? builder.Build() : new ConfigurationRoot(new[] { first, second, failing }));

            Assert.Equal(new[] { failure, disposalFailure }, exception.InnerExceptions);
            Assert.Equal(0, first.Token.RegistrationCount);
            Assert.Equal(0, second.Token.RegistrationCount);
            Assert.Equal(0, first.DisposeCount);
            Assert.Equal(0, second.DisposeCount);
        }

        [Fact]
        public void LoadAndCombineKeyValuePairsFromDifferentConfigurationProviders()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2:KeyInMem2", "ValueInMem2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3:KeyInMem3", "ValueInMem3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            var memVal1 = config["mem1:keyinmem1"];
            var memVal2 = config["Mem2:KeyInMem2"];
            var memVal3 = config["MEM3:KEYINMEM3"];

            // Assert
            Assert.Contains(memConfigSrc1, configurationBuilder.Sources);
            Assert.Contains(memConfigSrc2, configurationBuilder.Sources);
            Assert.Contains(memConfigSrc3, configurationBuilder.Sources);

            Assert.Equal("ValueInMem1", memVal1);
            Assert.Equal("ValueInMem2", memVal2);
            Assert.Equal("ValueInMem3", memVal3);

            Assert.Equal("ValueInMem1", config["mem1:keyinmem1"]);
            Assert.Equal("ValueInMem2", config["Mem2:KeyInMem2"]);
            Assert.Equal("ValueInMem3", config["MEM3:KEYINMEM3"]);
            Assert.Null(config["NotExist"]);
        }

        [Fact]
        public void GetChildKeys_CanChainEmptyKeys()
        {
            var input = new Dictionary<string, string>() { };
            for (int i = 0; i < 1000; i++)
            {
                input.Add(new string(' ', i), string.Empty);
            }

            IConfigurationRoot configurationRoot = new ConfigurationBuilder()
                .Add(new MemoryConfigurationSource
                {
                    InitialData = input
                })
                .Build();

            var chainedConfigurationSource = new ChainedConfigurationSource
            {
                Configuration = configurationRoot,
                ShouldDisposeConfiguration = false,
            };
            
            var chainedConfiguration = new ChainedConfigurationProvider(chainedConfigurationSource);
            IEnumerable<string> childKeys = chainedConfiguration.GetChildKeys(new string[0], null);
            Assert.Equal(1000, childKeys.Count());
            Assert.Equal(string.Empty, childKeys.First());
            Assert.Equal(999, childKeys.Last().Length);
        }

        [Fact]
        public void GetChildKeys_CanChainKeyWithNoDelimiter()
        {
            var input = new Dictionary<string, string>() { };
            for (int i = 1000; i < 2000; i++)
            {
                input.Add(i.ToString(), string.Empty);
            }

            IConfigurationRoot configurationRoot = new ConfigurationBuilder()
                .Add(new MemoryConfigurationSource
                {
                    InitialData = input
                })
                .Build();

            var chainedConfigurationSource = new ChainedConfigurationSource
            {
                Configuration = configurationRoot,
                ShouldDisposeConfiguration = false,
            };
            
            var chainedConfiguration = new ChainedConfigurationProvider(chainedConfigurationSource);
            IEnumerable<string> childKeys = chainedConfiguration.GetChildKeys(new string[0], null);
            Assert.Equal(1000, childKeys.Count());
            Assert.Equal("1000", childKeys.First());
            Assert.Equal("1999", childKeys.Last());
        }

        [Fact]
        public void CanChainConfiguration()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2:KeyInMem2", "ValueInMem2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3:KeyInMem3", "ValueInMem3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            var chained = new ConfigurationBuilder().AddConfiguration(config).Build();
            var memVal1 = chained["mem1:keyinmem1"];
            var memVal2 = chained["Mem2:KeyInMem2"];
            var memVal3 = chained["MEM3:KEYINMEM3"];

            // Assert

            Assert.Equal("ValueInMem1", memVal1);
            Assert.Equal("ValueInMem2", memVal2);
            Assert.Equal("ValueInMem3", memVal3);

            Assert.Null(chained["NotExist"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ChainedAsEnumerateFlattensIntoDictionaryTest(bool removePath)
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            var config = new ConfigurationBuilder()
                .AddConfiguration(configurationBuilder.Build())
                .Add(memConfigSrc3)
                .Build();
            var dict = config.AsEnumerable(makePathsRelative: removePath).ToDictionary(k => k.Key, v => v.Value);

            // Assert
            Assert.Equal("Value1", dict["Mem1"]);
            Assert.Equal("NoKeyValue1", dict["Mem1:"]);
            Assert.Equal("ValueDeep1", dict["Mem1:KeyInMem1:Deep1"]);
            Assert.Equal("ValueInMem2", dict["Mem2:KeyInMem2"]);
            Assert.Equal("Value2", dict["Mem2"]);
            Assert.Equal("NoKeyValue2", dict["Mem2:"]);
            Assert.Equal("ValueDeep2", dict["Mem2:KeyInMem2:Deep2"]);
            Assert.Equal("Value3", dict["Mem3"]);
            Assert.Equal("NoKeyValue3", dict["Mem3:"]);
            Assert.Equal("ValueInMem3", dict["Mem3:KeyInMem3"]);
            Assert.Equal("ValueDeep3", dict["Mem3:KeyInMem3:Deep3"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AsEnumerateFlattensIntoDictionaryTest(bool removePath)
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);
            var config = configurationBuilder.Build();
            var dict = config.AsEnumerable(makePathsRelative: removePath).ToDictionary(k => k.Key, v => v.Value);

            // Assert
            Assert.Equal("Value1", dict["Mem1"]);
            Assert.Equal("NoKeyValue1", dict["Mem1:"]);
            Assert.Equal("ValueDeep1", dict["Mem1:KeyInMem1:Deep1"]);
            Assert.Equal("ValueInMem2", dict["Mem2:KeyInMem2"]);
            Assert.Equal("Value2", dict["Mem2"]);
            Assert.Equal("NoKeyValue2", dict["Mem2:"]);
            Assert.Equal("ValueDeep2", dict["Mem2:KeyInMem2:Deep2"]);
            Assert.Equal("Value3", dict["Mem3"]);
            Assert.Equal("NoKeyValue3", dict["Mem3:"]);
            Assert.Equal("ValueInMem3", dict["Mem3:KeyInMem3"]);
            Assert.Equal("ValueDeep3", dict["Mem3:KeyInMem3:Deep3"]);
        }

        [Fact]
        public void AsEnumerateStripsKeyFromChildren()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem4", "ValueInMem4"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"},
                {"Mem3:KeyInMem3:Deep4", "ValueDeep4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            var dict = config.GetSection("Mem1").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(3, dict.Count);
            Assert.Equal("NoKeyValue1", dict[""]);
            Assert.Equal("ValueInMem1", dict["KeyInMem1"]);
            Assert.Equal("ValueDeep1", dict["KeyInMem1:Deep1"]);

            var dict2 = config.GetSection("Mem2").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(3, dict2.Count);
            Assert.Equal("NoKeyValue2", dict2[""]);
            Assert.Equal("ValueInMem2", dict2["KeyInMem2"]);
            Assert.Equal("ValueDeep2", dict2["KeyInMem2:Deep2"]);

            var dict3 = config.GetSection("Mem3").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(5, dict3.Count);
            Assert.Equal("NoKeyValue3", dict3[""]);
            Assert.Equal("ValueInMem3", dict3["KeyInMem3"]);
            Assert.Equal("ValueInMem4", dict3["KeyInMem4"]);
            Assert.Equal("ValueDeep3", dict3["KeyInMem3:Deep3"]);
            Assert.Equal("ValueDeep4", dict3["KeyInMem3:Deep4"]);
        }


        [Fact]
        public void NewConfigurationProviderOverridesOldOneWhenKeyIsDuplicated()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
                {
                    {"Key1:Key2", "ValueInMem1"}
                };
            var dic2 = new Dictionary<string, string>()
                {
                    {"Key1:Key2", "ValueInMem2"}
                };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            var config = configurationBuilder.Build();

            // Assert
            Assert.Equal("ValueInMem2", config["Key1:Key2"]);
        }

        [Fact]
        public void NewConfigurationRootMayBeBuiltFromExistingWithDuplicateKeys()
        {
            var configurationRoot = new ConfigurationBuilder()
                                    .AddInMemoryCollection(new Dictionary<string, string>
                                        {
                                            {"keya:keyb", "valueA"},
                                        })
                                    .AddInMemoryCollection(new Dictionary<string, string>
                                        {
                                            {"KEYA:KEYB", "valueB"}
                                        })
                                    .Build();
            var newConfigurationRoot = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationRoot.AsEnumerable())
                .Build();
            Assert.Equal("valueB", newConfigurationRoot["keya:keyb"]);
        }

        public class TestMemorySourceProvider : MemoryConfigurationProvider, IConfigurationSource
        {
            public TestMemorySourceProvider(Dictionary<string, string> initialData)
                : base(new MemoryConfigurationSource { InitialData = initialData })
            { }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                return this;
            }
        }

        [Fact]
        public void SettingValueUpdatesAllConfigurationProviders()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Key1", "Value1"},
                {"Key2", "Value2"}
            };

            var memConfigSrc1 = new TestMemorySourceProvider(dict);
            var memConfigSrc2 = new TestMemorySourceProvider(dict);
            var memConfigSrc3 = new TestMemorySourceProvider(dict);

            var configurationBuilder = new ConfigurationBuilder();

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            // Act
            config["Key1"] = "NewValue1";
            config["Key2"] = "NewValue2";

            var memConfigProvider1 = memConfigSrc1.Build(configurationBuilder);
            var memConfigProvider2 = memConfigSrc2.Build(configurationBuilder);
            var memConfigProvider3 = memConfigSrc3.Build(configurationBuilder);

            // Assert
            Assert.Equal("NewValue1", config["Key1"]);
            Assert.Equal("NewValue1", memConfigProvider1.Get("Key1"));
            Assert.Equal("NewValue1", memConfigProvider2.Get("Key1"));
            Assert.Equal("NewValue1", memConfigProvider3.Get("Key1"));
            Assert.Equal("NewValue2", config["Key2"]);
            Assert.Equal("NewValue2", memConfigProvider1.Get("Key2"));
            Assert.Equal("NewValue2", memConfigProvider2.Get("Key2"));
            Assert.Equal("NewValue2", memConfigProvider3.Get("Key2"));
        }

        [Fact]
        public void CanGetConfigurationSection()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Data:DB1:Connection1", "MemVal1"},
                {"Data:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"DataSource:DB2:Connection", "MemVal3"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Data", "MemVal4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            // Act
            var configFocus = config.GetSection("Data");

            var memVal1 = configFocus["DB1:Connection1"];
            var memVal2 = configFocus["DB1:Connection2"];
            var memVal3 = configFocus["DB2:Connection"];
            var memVal4 = configFocus["Source:DB2:Connection"];
            var memVal5 = configFocus.Value;

            // Assert
            Assert.Equal("MemVal1", memVal1);
            Assert.Equal("MemVal2", memVal2);
            Assert.Equal("MemVal4", memVal5);

            Assert.Equal("MemVal1", configFocus["DB1:Connection1"]);
            Assert.Equal("MemVal2", configFocus["DB1:Connection2"]);
            Assert.Null(configFocus["DB2:Connection"]);
            Assert.Null(configFocus["Source:DB2:Connection"]);
            Assert.Equal("MemVal4", configFocus.Value);
        }

        [Fact]
        public void CanGetConnectionStrings()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"ConnectionStrings:DB1:Connection1", "MemVal1"},
                {"ConnectionStrings:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"ConnectionStrings:DB2:Connection", "MemVal3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            var config = configurationBuilder.Build();

            // Act
            var memVal1 = config.GetConnectionString("DB1:Connection1");
            var memVal2 = config.GetConnectionString("DB1:Connection2");
            var memVal3 = config.GetConnectionString("DB2:Connection");

            // Assert
            Assert.Equal("MemVal1", memVal1);
            Assert.Equal("MemVal2", memVal2);
            Assert.Equal("MemVal3", memVal3);
        }

        [Fact]
        public void CanGetConfigurationChildren()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Data:DB1:Connection1", "MemVal1"},
                {"Data:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Data:DB2Connection", "MemVal3"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"DataSource:DB3:Connection", "MemVal4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            // Act
            var configSections = config.GetSection("Data").GetChildren().ToList();

            // Assert
            Assert.Equal(2, configSections.Count());
            Assert.Equal("MemVal1", configSections.FirstOrDefault(c => c.Key == "DB1")["Connection1"]);
            Assert.Equal("MemVal2", configSections.FirstOrDefault(c => c.Key == "DB1")["Connection2"]);
            Assert.Equal("MemVal3", configSections.FirstOrDefault(c => c.Key == "DB2Connection").Value);
            Assert.False(configSections.Exists(c => c.Key == "DB3"));
            Assert.False(configSections.Exists(c => c.Key == "DB3"));
        }

        [Fact]
        public void SourcesReturnsAddedConfigurationProviders()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem:KeyInMem", "MemVal"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dict };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dict };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dict };

            var srcSet = new HashSet<IConfigurationSource>()
            {
                memConfigSrc1,
                memConfigSrc2,
                memConfigSrc3
            };

            var configurationBuilder = new ConfigurationBuilder();

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var config = configurationBuilder.Build();

            // Assert
            Assert.Equal(new[] { memConfigSrc1, memConfigSrc2, memConfigSrc3 }, configurationBuilder.Sources);
        }

        [Fact]
        public void SetValueThrowsExceptionNoSourceRegistered()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            var expectedMsg = SR.Error_NoSources;

            // Act
            var ex = Assert.Throws<InvalidOperationException>(() => config["Title"] = "Welcome");

            // Assert
            Assert.Equal(expectedMsg, ex.Message);
        }

        [Fact]
        public void SameReloadTokenIsReturnedRepeatedly()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            // Act
            var token1 = config.GetReloadToken();
            var token2 = config.GetReloadToken();

            // Assert
            Assert.Same(token1, token2);
        }

        [Fact]
        public void DifferentReloadTokenReturnedAfterReloading()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            // Act
            var token1 = config.GetReloadToken();
            var token2 = config.GetReloadToken();
            config.Reload();
            var token3 = config.GetReloadToken();
            var token4 = config.GetReloadToken();

            // Assert
            Assert.Same(token1, token2);
            Assert.Same(token3, token4);
            Assert.NotSame(token1, token3);
        }

        [Fact]
        public void TokenTriggeredWhenReloadOccurs()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            // Act
            var token1 = config.GetReloadToken();
            var hasChanged1 = token1.HasChanged;
            config.Reload();
            var hasChanged2 = token1.HasChanged;

            // Assert
            Assert.False(hasChanged1);
            Assert.True(hasChanged2);
        }

        [Fact]
        public void MultipleCallbacksCanBeRegisteredToReload()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            // Act
            var token1 = config.GetReloadToken();
            var called1 = 0;
            token1.RegisterChangeCallback(_ => called1++, state: null);
            var called2 = 0;
            token1.RegisterChangeCallback(_ => called2++, state: null);

            // Assert
            Assert.Equal(0, called1);
            Assert.Equal(0, called2);

            config.Reload();
            Assert.Equal(1, called1);
            Assert.Equal(1, called2);

            var token2 = config.GetReloadToken();
            var cleanup1 = token2.RegisterChangeCallback(_ => called1++, state: null);
            token2.RegisterChangeCallback(_ => called2++, state: null);

            cleanup1.Dispose();

            config.Reload();
            Assert.Equal(1, called1);
            Assert.Equal(2, called2);
        }

        [Fact]
        public void AsyncLocalsNotCapturedAndRestoredConfigurationReloadToken()
        {
            // Capture clean context
            var executionContext = ExecutionContext.Capture();

            var configurationReloadToken = new ConfigurationReloadToken();
            var executed = false;

            // Set AsyncLocal
            var asyncLocal = new AsyncLocal<int>();
            asyncLocal.Value = 1;

            // Register Callback
            configurationReloadToken.RegisterChangeCallback(al =>
            {
                // AsyncLocal not set, when run on clean context
                // A suppressed flow runs in current context, rather than restoring the captured context
                Assert.Equal(0, ((AsyncLocal<int>)al).Value);
                executed = true;
            }, asyncLocal);

            // AsyncLocal should still be set
            Assert.Equal(1, asyncLocal.Value);

            // Check AsyncLocal is not restored by running on clean context
            ExecutionContext.Run(executionContext, crt => ((ConfigurationReloadToken)crt).OnReload(), configurationReloadToken);

            // AsyncLocal should still be set
            Assert.Equal(1, asyncLocal.Value);
            Assert.True(executed);
        }

        [Fact]
        public void NewTokenAfterReloadIsNotChanged()
        {
            // Arrange
            var configurationBuilder = new ConfigurationBuilder();
            var config = configurationBuilder.Build();

            // Act
            var token1 = config.GetReloadToken();
            var hasChanged1 = token1.HasChanged;
            config.Reload();
            var hasChanged2 = token1.HasChanged;
            var token2 = config.GetReloadToken();
            var hasChanged3 = token2.HasChanged;

            // Assert
            Assert.False(hasChanged1);
            Assert.True(hasChanged2);
            Assert.False(hasChanged3);
            Assert.NotSame(token1, token2);
        }

        [Fact]
        public void KeyStartingWithColonMeansFirstSectionHasEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                [":Key2"] = "value"
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal(string.Empty, children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal("Key2", children.First().GetChildren().First().Key);
        }

        [Fact]
        public void KeyWithDoubleColonHasSectionWithEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                ["Key1::Key3"] = "value"
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal("Key1", children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal(string.Empty, children.First().GetChildren().First().Key);
            Assert.Single(children.First().GetChildren().First().GetChildren());
            Assert.Equal("Key3", children.First().GetChildren().First().GetChildren().First().Key);
        }

        [Fact]
        public void KeyEndingWithColonMeansLastSectionHasEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                ["Key1:"] = "value"
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal("Key1", children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal(string.Empty, children.First().GetChildren().First().Key);
        }

        [Fact]
        public void SectionWithValueExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var sectionExists1 = config.GetSection("Mem1").Exists();
            var sectionExists2 = config.GetSection("Mem1:KeyInMem1").Exists();
            var sectionNotExists = config.GetSection("Mem2").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
            Assert.False(sectionNotExists);
        }

        [Fact]
        public void SectionGetRequiredSectionSuccess()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            IConfigurationRoot config = configurationBuilder.Build();

            // Act
            var sectionExists1 = config.GetRequiredSection("Mem1").Exists();
            var sectionExists2 = config.GetRequiredSection("Mem1:KeyInMem1").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
        }

        [Fact]
        public void SectionGetRequiredSectionMissingThrowException()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:Deep1", "Value1"},
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            IConfigurationRoot config = configurationBuilder.Build();

            Assert.Throws<InvalidOperationException>(() => config.GetRequiredSection("Mem2"));
            Assert.Throws<InvalidOperationException>(() => config.GetRequiredSection("Mem1:Deep2"));
        }

        [Fact]
        public void SectionGetRequiredSectionNullThrowException()
        {
            IConfigurationRoot config = null;
            Assert.Throws<ArgumentNullException>(() => config.GetRequiredSection("Mem1"));
        }

        [Fact]
        public void SectionWithChildrenExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"},
                {"Mem2:KeyInMem2:Deep1", "ValueDeep2"}
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var sectionExists1 = config.GetSection("Mem1").Exists();
            var sectionExists2 = config.GetSection("Mem2").Exists();
            var sectionNotExists = config.GetSection("Mem3").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
            Assert.False(sectionNotExists);
        }

        [Theory]
        [InlineData("Value1")]
        [InlineData("")]
        public void KeyWithValueAndWithoutChildrenExistsAsSection(string value)
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", value}
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var sectionExists = config.GetSection("Mem1").Exists();

            // Assert
            Assert.True(sectionExists);
        }

        [Fact]
        public void KeyWithNullValueAndWithoutChildrenIsASectionButNotExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", null}
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var sections = config.GetChildren();
            var sectionExists = config.GetSection("Mem1").Exists();
            var sectionChildren = config.GetSection("Mem1").GetChildren();

            // Assert
            Assert.Single(sections, section => section.Key == "Mem1");
            Assert.False(sectionExists);
            Assert.Empty(sectionChildren);
        }

        [Fact]
        public void SectionWithChildrenHasNullValue()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"},
            };
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(dict);
            var config = configurationBuilder.Build();

            // Act
            var sectionValue = config.GetSection("Mem1").Value;

            // Assert
            Assert.Null(sectionValue);
        }

        [Fact]
        public void NullSectionDoesNotExist()
        {
            // Arrange
            // Act
            var sectionExists = ConfigurationExtensions.Exists(null);

            // Assert
            Assert.False(sectionExists);
        }

        internal class NullReloadTokenConfigSource : IConfigurationSource, IConfigurationProvider
        {
            public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath) => throw new NotImplementedException();
            public Primitives.IChangeToken GetReloadToken() => null;
            public void Load() { }
            public void Set(string key, string value) => throw new NotImplementedException();
            public bool TryGet(string key, out string value) => throw new NotImplementedException();
            public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
        }

        [Fact]
        public void ProviderWithNullReloadToken()
        {
            // Arrange
            var builder = new ConfigurationBuilder();
            builder.Add(new NullReloadTokenConfigSource());

            // Act
            var config = builder.Build();

            // Assert
            Assert.NotNull(config);
        }
    }

    public enum ConfigurationFailureStage
    {
        Build,
        Load,
        GetReloadToken,
        RegisterChangeCallback
    }

    internal sealed class ConfigurationFailureSource(ConfigurationFailureProvider provider) : IConfigurationSource
    {
        public ConfigurationFailureProvider Provider { get; set; } = provider;
        public Exception BuildException { get; set; }
        public Action<IConfigurationBuilder> BeforeBuild { get; set; }
        public int BuildCount { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            BuildCount++;
            BeforeBuild?.Invoke(builder);
            if (BuildException is not null)
            {
                throw BuildException;
            }

            return Provider;
        }

        public Exception SetFailure(ConfigurationFailureStage stage)
        {
            var exception = new InvalidOperationException("Configuration initialisation failed.");
            switch (stage)
            {
                case ConfigurationFailureStage.Build:
                    BuildException = exception;
                    break;
                case ConfigurationFailureStage.Load:
                    Provider.LoadException = exception;
                    break;
                case ConfigurationFailureStage.GetReloadToken:
                    Provider.GetReloadTokenException = exception;
                    break;
                case ConfigurationFailureStage.RegisterChangeCallback:
                    Provider.Token.RegisterException = exception;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage));
            }

            return exception;
        }

        public void ClearFailure()
        {
            BuildException = null;
            Provider.LoadException = null;
            Provider.GetReloadTokenException = null;
            Provider.Token.RegisterException = null;
        }
    }

    internal sealed class OwnedConfigurationFailureSource : IConfigurationSource, IConfigurationSourceWithOwnedProviders
    {
        public Action<ConfigurationFailureProvider> Configure { get; set; }
        public ConfigurationFailureProvider Provider { get; private set; }

        bool IConfigurationSourceWithOwnedProviders.OwnsProviders => true;

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            Provider = new ConfigurationFailureProvider();
            Configure?.Invoke(Provider);
            return Provider;
        }
    }

    internal sealed class ConfigurationFailureProvider : ConfigurationProvider, IConfigurationProvider, IDisposable
    {
        public ConfigurationFailureProvider() => Set("key", "value");

        public ConfigurationFailureToken Token { get; private set; } = new();
        public Exception LoadException { get; set; }
        public Exception GetReloadTokenException { get; set; }
        public Exception DisposeException { get; set; }
        public int DisposeCount { get; private set; }

        public override void Load()
        {
            if (LoadException is not null)
            {
                throw LoadException;
            }
        }

        IChangeToken IConfigurationProvider.GetReloadToken()
        {
            if (GetReloadTokenException is not null)
            {
                throw GetReloadTokenException;
            }

            return Token;
        }

        public void Reload()
        {
            ConfigurationFailureToken previousToken = Token;
            Token = new ConfigurationFailureToken();
            previousToken.OnReload();
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }

    internal sealed class ConfigurationFailureToken : IChangeToken
    {
        private readonly ConfigurationReloadToken _token = new();

        public int RegistrationCount { get; private set; }
        public Exception RegisterException { get; set; }
        public Exception DisposeException { get; set; }
        public bool HasChanged => _token.HasChanged;
        public bool ActiveChangeCallbacks => true;

        public IDisposable RegisterChangeCallback(Action<object> callback, object state)
        {
            if (RegisterException is not null)
            {
                throw RegisterException;
            }

            IDisposable registration = _token.RegisterChangeCallback(callback, state);
            RegistrationCount++;
            return new Registration(this, registration);
        }

        public void OnReload() => _token.OnReload();

        private sealed class Registration(ConfigurationFailureToken token, IDisposable registration) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    token.RegistrationCount--;
                    registration.Dispose();
                    if (token.DisposeException is not null)
                    {
                        throw token.DisposeException;
                    }
                }
            }
        }
    }
}
