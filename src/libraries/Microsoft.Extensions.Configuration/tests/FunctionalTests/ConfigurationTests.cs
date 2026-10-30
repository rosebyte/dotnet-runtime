// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration.Ini;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.Configuration.Xml;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Microsoft.Extensions.Configuration.Test
{
    public class ConfigurationTests : IDisposable
    {
        private const int _retries = 150;
        private const int _msDelay = 200;

        private static readonly TimeSpan s_maxWaitForReload = TimeSpan.FromSeconds(30);

        private readonly DisposableFileSystem _fileSystem;
        private readonly PhysicalFileProvider _fileProvider;
        private readonly string _basePath;
        private readonly string _iniFile;
        private readonly string _xmlFile;
        private readonly string _jsonFile;
        private static readonly string _iniConfigFileContent =
            @"IniKey1=IniValue1
[IniKey2]
# Comments
IniKey3=IniValue2
; Comments
IniKey4=IniValue3
IniKey5:IniKey6=IniValue4
/Comments
[CommonKey1:CommonKey2]
IniKey7=IniValue5
CommonKey3:CommonKey4=IniValue6";
        private static readonly string _xmlConfigFileContent =
            @"<settings XmlKey1=""XmlValue1"">
  <!-- Comments -->
  <XmlKey2 XmlKey3=""XmlValue2"">
    <!-- Comments -->
    <XmlKey4>XmlValue3</XmlKey4>
    <XmlKey5 Name=""XmlKey6"">XmlValue4</XmlKey5>
  </XmlKey2>
  <CommonKey1 Name=""CommonKey2"" XmlKey7=""XmlValue5"">
    <!-- Comments -->
    <CommonKey3 CommonKey4=""XmlValue6"" />
  </CommonKey1>
</settings>";
        private static readonly string _jsonConfigFileContent =
            @"{
  ""JsonKey1"": ""JsonValue1"",
  ""Json.Key2"": {
    ""JsonKey3"": ""JsonValue2"",
    ""Json.Key4"": ""JsonValue3"",
    ""JsonKey5:JsonKey6"": ""JsonValue4""
  },
  ""CommonKey1"": {
    ""CommonKey2"": {
      ""JsonKey7"": ""JsonValue5"",
      ""CommonKey3:CommonKey4"": ""JsonValue6""
    }
  }
}";
        private static readonly Dictionary<string, string> _memConfigContent = new Dictionary<string, string>
            {
                { "MemKey1", "MemValue1" },
                { "MemKey2:MemKey3", "MemValue2" },
                { "MemKey2:MemKey4", "MemValue3" },
                { "MemKey2:MemKey5:MemKey6", "MemValue4" },
                { "CommonKey1:CommonKey2:MemKey7", "MemValue5" },
                { "CommonKey1:CommonKey2:CommonKey3:CommonKey4", "MemValue6" }
            };

        public ConfigurationTests()
        {
            _fileSystem = new DisposableFileSystem();
            _fileProvider = new PhysicalFileProvider(_fileSystem.RootPath);
            _basePath = AppContext.BaseDirectory ?? string.Empty;

            _iniFile = Path.GetRandomFileName();
            _xmlFile = Path.GetRandomFileName();
            _jsonFile = Path.GetRandomFileName();
        }

        [Fact]
        public void ThrowsOnFileNotFoundWhenNotIgnored()
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddJsonFile(c =>
            {
                c.Path = Path.Combine(_fileSystem.RootPath, _jsonFile);
            });

            Assert.Throws<FileNotFoundException>(() => configurationBuilder.Build());
        }

        [Fact]
        public void CanHandleExceptionIfFileNotFound()
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddJsonFile(c =>
            {
                c.Path = Path.Combine(_fileSystem.RootPath, _jsonFile);
                c.OnLoadException = e =>
                {
                    e.Ignore = true;
                    var exception = e.Exception as FileNotFoundException;
                    Assert.NotNull(exception);
                };
            });

            configurationBuilder.Build();
        }

        [Fact]
        public void MissingFileIncludesAbsolutePathIfPhysicalFileProvider()
        {
            var error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddIniFile("missing.ini").Build());
            Assert.True(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddJsonFile("missing.json").Build());
            Assert.True(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddXmlFile("missing.xml").Build());
            Assert.True(error.Message.Contains(_basePath), error.Message);
        }

        private class NotVeryGoodFileProvider : IFileProvider
        {
            public IDirectoryContents GetDirectoryContents(string subpath) => null;
            public IFileInfo GetFileInfo(string subpath) => null;
            public IChangeToken Watch(string filter) => null;
        }

        private class MissingFile : IFileInfo
        {
            public bool Exists => false;
            public bool IsDirectory => throw new NotImplementedException();
            public DateTimeOffset LastModified => throw new NotImplementedException();
            public long Length => throw new NotImplementedException();
            public string Name => throw new NotImplementedException();
            public string PhysicalPath => throw new NotImplementedException();
            public Stream CreateReadStream() => throw new NotImplementedException();
        }

        private class AlwaysMissingFileProvider : IFileProvider
        {
            public IDirectoryContents GetDirectoryContents(string subpath) => null;
            public IFileInfo GetFileInfo(string subpath) => null;
            public IChangeToken Watch(string filter) => null;
        }

        private void WriteTestFiles()
        {
            _fileSystem.WriteFile(_iniFile, _iniConfigFileContent);
            _fileSystem.WriteFile(_xmlFile, _xmlConfigFileContent);
            _fileSystem.WriteFile(_jsonFile, _jsonConfigFileContent);
        }

        private IConfigurationBuilder CreateBuilder()
        {
            return new ConfigurationBuilder().SetFileProvider(_fileProvider);
        }

        [Fact]
        public void MissingFileDoesNotIncludesAbsolutePathIfWithNullFileInfo()
        {
            var provider = new NotVeryGoodFileProvider();
            var error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddIniFile(provider, "missing.ini", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddJsonFile(provider, "missing.json", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddXmlFile(provider, "missing.xml", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);
        }

        [Fact]
        public void MissingFileDoesNotIncludesAbsolutePathIfWithNoPhysicalPath()
        {
            var provider = new AlwaysMissingFileProvider();
            var error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddIniFile(provider, "missing.ini", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddJsonFile(provider, "missing.json", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);

            error = Assert.Throws<FileNotFoundException>(
                () => new ConfigurationBuilder().AddXmlFile(provider, "missing.xml", optional: false, reloadOnChange: false).Build());
            Assert.False(error.Message.Contains(_basePath), error.Message);
        }

        [Fact]
        public void LoadAndCombineKeyValuePairsFromDifferentConfigurationProviders()
        {
            WriteTestFiles();

            var config = CreateBuilder()
                .AddIniFile(_iniFile)
                .AddJsonFile(_jsonFile)
                .AddXmlFile(_xmlFile)
                .AddInMemoryCollection(_memConfigContent)
                .Build();

            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("IniValue2", config["IniKey2:IniKey3"]);
            Assert.Equal("IniValue3", config["IniKey2:IniKey4"]);
            Assert.Equal("IniValue4", config["IniKey2:IniKey5:IniKey6"]);
            Assert.Equal("IniValue5", config["CommonKey1:CommonKey2:IniKey7"]);

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("JsonValue2", config["Json.Key2:JsonKey3"]);
            Assert.Equal("JsonValue3", config["Json.Key2:Json.Key4"]);
            Assert.Equal("JsonValue4", config["Json.Key2:JsonKey5:JsonKey6"]);
            Assert.Equal("JsonValue5", config["CommonKey1:CommonKey2:JsonKey7"]);

            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey2:XmlKey3"]);
            Assert.Equal("XmlValue3", config["XmlKey2:XmlKey4"]);
            Assert.Equal("XmlValue4", config["XmlKey2:XmlKey5:XmlKey6"]);
            Assert.Equal("XmlValue5", config["CommonKey1:CommonKey2:XmlKey7"]);

            Assert.Equal("MemValue1", config["MemKey1"]);
            Assert.Equal("MemValue2", config["MemKey2:MemKey3"]);
            Assert.Equal("MemValue3", config["MemKey2:MemKey4"]);
            Assert.Equal("MemValue4", config["MemKey2:MemKey5:MemKey6"]);
            Assert.Equal("MemValue5", config["CommonKey1:CommonKey2:MemKey7"]);

            Assert.Equal("MemValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);
        }

        [Fact]
        public void LoadAndCombineKeyValuePairsFromDifferentConfigurationProvidersWithAbsolutePath()
        {
            WriteTestFiles();

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddIniFile(Path.Combine(_fileSystem.RootPath, _iniFile));
            configurationBuilder.AddJsonFile(Path.Combine(_fileSystem.RootPath, _jsonFile));
            configurationBuilder.AddXmlFile(Path.Combine(_fileSystem.RootPath, _xmlFile));
            configurationBuilder.AddInMemoryCollection(_memConfigContent);

            var config = configurationBuilder.Build();

            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("IniValue2", config["IniKey2:IniKey3"]);
            Assert.Equal("IniValue3", config["IniKey2:IniKey4"]);
            Assert.Equal("IniValue4", config["IniKey2:IniKey5:IniKey6"]);
            Assert.Equal("IniValue5", config["CommonKey1:CommonKey2:IniKey7"]);

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("JsonValue2", config["Json.Key2:JsonKey3"]);
            Assert.Equal("JsonValue3", config["Json.Key2:Json.Key4"]);
            Assert.Equal("JsonValue4", config["Json.Key2:JsonKey5:JsonKey6"]);
            Assert.Equal("JsonValue5", config["CommonKey1:CommonKey2:JsonKey7"]);

            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey2:XmlKey3"]);
            Assert.Equal("XmlValue3", config["XmlKey2:XmlKey4"]);
            Assert.Equal("XmlValue4", config["XmlKey2:XmlKey5:XmlKey6"]);
            Assert.Equal("XmlValue5", config["CommonKey1:CommonKey2:XmlKey7"]);

            Assert.Equal("MemValue1", config["MemKey1"]);
            Assert.Equal("MemValue2", config["MemKey2:MemKey3"]);
            Assert.Equal("MemValue3", config["MemKey2:MemKey4"]);
            Assert.Equal("MemValue4", config["MemKey2:MemKey5:MemKey6"]);
            Assert.Equal("MemValue5", config["CommonKey1:CommonKey2:MemKey7"]);

            Assert.Equal("MemValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);
        }

        [Fact]
        public void CanOverrideValuesWithNewConfigurationProvider()
        {
            WriteTestFiles();

            var configurationBuilder = CreateBuilder();

            configurationBuilder.AddIniFile(_iniFile);
            var config = configurationBuilder.Build();
            Assert.Equal("IniValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);

            configurationBuilder.AddJsonFile(_jsonFile);
            config = configurationBuilder.Build();
            Assert.Equal("JsonValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);

            configurationBuilder.AddXmlFile(_xmlFile);
            config = configurationBuilder.Build();
            Assert.Equal("XmlValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);

            configurationBuilder.AddInMemoryCollection(_memConfigContent);
            config = configurationBuilder.Build();
            Assert.Equal("MemValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);
        }

        private IConfigurationRoot BuildConfig()
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddIniFile(Path.GetFileName(_iniFile));
            configurationBuilder.AddJsonFile(Path.GetFileName(_jsonFile));
            configurationBuilder.AddXmlFile(Path.GetFileName(_xmlFile));
            return configurationBuilder.Build();
        }

        public class TestIniSourceProvider : IniConfigurationProvider, IConfigurationSource
        {
            public TestIniSourceProvider(string path)
                : base(new IniConfigurationSource { Path = path })
            { }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                Source.FileProvider = builder.GetFileProvider();
                return this;
            }
        }

        public class TestJsonSourceProvider : JsonConfigurationProvider, IConfigurationSource
        {
            public TestJsonSourceProvider(string path)
                : base(new JsonConfigurationSource { Path = path })
            { }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                Source.FileProvider = builder.GetFileProvider();
                return this;
            }
        }

        public class TestXmlSourceProvider : XmlConfigurationProvider, IConfigurationSource
        {
            public TestXmlSourceProvider(string path)
                : base(new XmlConfigurationSource { Path = path })
            { }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                Source.FileProvider = builder.GetFileProvider();
                return this;
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/60583", TestPlatforms.iOS | TestPlatforms.tvOS)]
        public void OnLoadErrorWillBeCalledOnJsonParseError()
        {
            _fileSystem.WriteFile(Path.Combine(_basePath, "error.json"), @"{""JsonKey1"": ", absolute: true);

            FileConfigurationProvider provider = null;
            Exception jsonError = null;
            Action<FileLoadExceptionContext> jsonLoadError = c =>
            {
                jsonError = c.Exception;
                provider = c.Provider;
            };

            try
            {
                new ConfigurationBuilder()
                    .AddJsonFile("error.json")
                    .SetFileLoadExceptionHandler(jsonLoadError)
                    .Build();
            }
            catch (InvalidDataException e)
            {
                Assert.Equal(e, jsonError);
            }

            Assert.NotNull(provider);
        }

        [Fact]
        public void OnLoadErrorWillBeCalledOnXmlParseError()
        {
            _fileSystem.WriteFile("error.xml", @"gobblygook");

            FileConfigurationProvider provider = null;
            Exception error = null;
            Action<FileLoadExceptionContext> loadError = c =>
            {
                error = c.Exception;
                provider = c.Provider;
            };

            try
            {
                CreateBuilder().AddJsonFile("error.xml")
                    .SetFileLoadExceptionHandler(loadError)
                    .Build();
            }
            catch (InvalidDataException e)
            {
                Assert.Equal(e, error);
            }

            Assert.NotNull(provider);
        }

        [Fact]
        public void OnLoadErrorWillBeCalledOnIniLoadError()
        {
            _fileSystem.WriteFile("error.ini", @"IniKey1=IniValue1
IniKey1=IniValue2");

            FileConfigurationProvider provider = null;
            Exception error = null;
            Action<FileLoadExceptionContext> loadError = c =>
            {
                error = c.Exception;
                provider = c.Provider;
            };

            try
            {
                CreateBuilder().AddIniFile("error.ini")
                    .SetFileLoadExceptionHandler(loadError)
                    .Build();
            }
            catch (InvalidDataException e)
            {
                Assert.Equal(e, error);
            }

            Assert.NotNull(provider);
        }

        [Fact]
        public void CanSetValuesAndReloadValues()
        {
            WriteTestFiles();

            var configurationBuilder = CreateBuilder();
            configurationBuilder.Add(new TestIniSourceProvider(_iniFile));
            configurationBuilder.Add(new TestJsonSourceProvider(_jsonFile));
            configurationBuilder.Add(new TestXmlSourceProvider(_xmlFile));

            var config = configurationBuilder.Build();

            // Set value
            config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"] = "NewValue";

            // All config sources must be updated
            foreach (var provider in configurationBuilder.Sources)
            {
                Assert.Equal("NewValue",
                    (provider as FileConfigurationProvider).Get("CommonKey1:CommonKey2:CommonKey3:CommonKey4"));
            }

            // Recover values by reloading
            config.Reload();

            Assert.Equal("XmlValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);

            // Set value with indexer
            config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"] = "NewValue";

            // All config sources must be updated
            foreach (var provider in configurationBuilder.Sources)
            {
                Assert.Equal("NewValue",
                    (provider as FileConfigurationProvider).Get("CommonKey1:CommonKey2:CommonKey3:CommonKey4"));
            }

            // Recover values by reloading
            config.Reload();
            Assert.Equal("XmlValue6", config["CommonKey1:CommonKey2:CommonKey3:CommonKey4"]);
        }

        [Fact]
        public async Task ReloadOnChangeWorksAfterError()
        {
            _fileSystem.WriteFile("reload.json", @"{""JsonKey1"": ""JsonValue1""}");

            var config = CreateBuilder()
                .AddJsonFile("reload.json", optional: false, reloadOnChange: true)
                .Build();

            Assert.Equal("JsonValue1", config["JsonKey1"]);

            // Introduce an error and make sure the old key is removed
            await WriteFilesForReload(
                config,
                () => _fileSystem.WriteFile("reload.json", @"{""JsonKey1"": "),
                Path.Combine(_fileSystem.RootPath, "reload.json"));

            await WaitForChange(
                () => config["JsonKey1"] == null,
                "Notification failed for loading after error.");

            Assert.Null(config["JsonKey1"]);

            // Update the file again to make sure the config is updated
            await WriteFilesForReload(
                config,
                () => _fileSystem.WriteFile("reload.json", @"{""JsonKey1"": ""JsonValue2""}"),
                Path.Combine(_fileSystem.RootPath, "reload.json"));

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue2",
                "Notification failed for updating after error.");

            Assert.Equal("JsonValue2", config["JsonKey1"]);
        }

        [Fact]
        public void BuildThrowsOnInvalidData()
        {
            const string FileName = $"{nameof(BuildThrowsOnInvalidData)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ");

            bool callbackCalled = false;
            Exception failureException = null;

            Assert.Throws<InvalidDataException>(() => CreateBuilder()
                .AddJsonFile(s =>
                {
                    s.Path = FileName;
                    s.OnLoadException = c =>
                    {
                        callbackCalled = true;
                        failureException = c.Exception;
                        // c.Ignore stays false. Exception propagates from Build()
                    };
                })
                .Build());

            Assert.True(callbackCalled);
            Assert.IsType<InvalidDataException>(failureException);
        }

        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void BuildThrowsOnIoError()
        {
            const string FileName = $"{nameof(BuildThrowsOnIoError)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ""JsonValue1"" }");

            using (_fileSystem.LockFileReading(FileName))
            {
                bool callbackCalled = false;
                Exception failureException = null;

                Assert.Throws<IOException>(() => CreateBuilder()
                    .AddJsonFile(s =>
                    {
                        s.Path = FileName;
                        s.OnLoadException = c =>
                        {
                            callbackCalled = true;
                            failureException = c.Exception;
                            // c.Ignore stays false. Exception propagates from Build()
                        };
                    })
                    .Build());

                Assert.True(callbackCalled);
                Assert.IsType<IOException>(failureException);
            }
        }

        [Theory]
        [MemberData(nameof(FileSourceFailureStages))]
        public void FailedBuilderDisposesFreshStandardFileProviderCandidates(
            FileSourceKind sourceKind,
            TestFailureStage failureStage)
        {
            var fileProvider = new TrackingFileProvider();
            var failureSource = new FaultingConfigurationSource();
            InvalidOperationException failure = failureSource.FailAt(failureStage);
            var builder = new ConfigurationBuilder();
            builder.Add(CreateFileSource(sourceKind, fileProvider, "first"));
            builder.Add(CreateFileSource(sourceKind, fileProvider, "second"));
            builder.Add(failureSource);
            builder.Add(CreateFileSource(sourceKind, fileProvider, "last"));

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Build()));
            Assert.Equal(failureStage == TestFailureStage.Build ? 2 : 3, fileProvider.WatchCallCount);
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
            Assert.All(failureSource.Providers, provider => Assert.Equal(0, provider.DisposeCount));
        }

        [Theory]
        [InlineData(FileSourceKind.Json)]
        [InlineData(FileSourceKind.Ini)]
        [InlineData(FileSourceKind.Xml)]
        public void FailedManagerAddDisposesStandardFileProviderCandidateAndAllowsRetry(FileSourceKind sourceKind)
        {
            var fileProvider = new TrackingFileProvider();
            FileConfigurationSource source = CreateFileSource(sourceKind, fileProvider, "missing", optional: false);
            var manager = new ConfigurationManager();

            Assert.Throws<FileNotFoundException>(() => manager.Sources.Add(source));
            Assert.DoesNotContain(source, manager.Sources);
            Assert.Equal(1, fileProvider.WatchCallCount);
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);

            source.Optional = true;
            manager.Sources.Add(source);
            Assert.Contains(source, manager.Sources);
            Assert.Equal(2, fileProvider.WatchCallCount);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);

            fileProvider.SetContent(GetFileContent(sourceKind, "working"));
            ((IConfigurationRoot)manager).Reload();
            Assert.Equal("working", manager["Key"]);

            manager.Dispose();
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Theory]
        [InlineData(FileSourceKind.Json)]
        [InlineData(FileSourceKind.Ini)]
        [InlineData(FileSourceKind.Xml)]
        public void FailedManagerAddAfterDisposeDisposesStandardFileProviderCandidate(FileSourceKind sourceKind)
        {
            var fileProvider = new TrackingFileProvider();
            FileConfigurationSource source = CreateFileSource(sourceKind, fileProvider, "missing");
            var manager = new ConfigurationManager();
            manager.Dispose();

            Assert.Throws<ObjectDisposedException>(() => manager.Sources.Add(source));
            Assert.DoesNotContain(source, manager.Sources);
            Assert.Equal(1, fileProvider.WatchCallCount);
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Theory]
        [InlineData(FileSourceKind.Json)]
        [InlineData(FileSourceKind.Ini)]
        [InlineData(FileSourceKind.Xml)]
        public void FailedManagerAddPreservesProviderPublishedReentrantly(FileSourceKind sourceKind)
        {
            var fileProvider = new TrackingFileProvider();
            FileConfigurationSource source = CreateFileSource(sourceKind, fileProvider, "missing", optional: false);
            var manager = new ConfigurationManager();
            var failure = new InvalidOperationException("Initial load failed after reentrant publication.");
            FileConfigurationProvider publishedProvider = null;
            source.OnLoadException = context =>
            {
                source.OnLoadException = null;
                source.Optional = true;
                publishedProvider = context.Provider;
                manager.Sources.Add(new CachedProviderSource(publishedProvider));
                throw failure;
            };

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => manager.Sources.Add(source)));
            Assert.DoesNotContain(source, manager.Sources);
            Assert.Contains(publishedProvider, ((IConfigurationRoot)manager).Providers);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);

            fileProvider.SetContent(GetFileContent(sourceKind, "recovered"));
            ((IConfigurationRoot)manager).Reload();
            Assert.Equal("recovered", manager["Key"]);

            manager.Dispose();
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Theory]
        [MemberData(nameof(FileSourceFailureStages))]
        public void FailedManagerSourceReloadDisposesCandidateAndPreservesActiveFileProvider(
            FileSourceKind sourceKind,
            TestFailureStage failureStage)
        {
            var fileProvider = new TrackingFileProvider();
            fileProvider.SetContent(GetFileContent(sourceKind, "initial"));
            FileConfigurationSource source = CreateFileSource(sourceKind, fileProvider, "settings", optional: false);
            var failureSource = new FaultingConfigurationSource();
            var manager = new ConfigurationManager();
            IConfigurationBuilder builder = manager;
            manager.Sources.Add(source);
            manager.Sources.Add(failureSource);
            FileConfigurationProvider activeProvider = Assert.Single(
                ((IConfigurationRoot)manager).Providers.OfType<FileConfigurationProvider>());
            FaultingConfigurationProvider activeFailureProvider = Assert.Single(failureSource.Providers);
            InvalidOperationException failure = failureSource.FailAt(failureStage);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Properties["failed"] = true));
            Assert.Same(activeProvider, Assert.Single(
                ((IConfigurationRoot)manager).Providers.OfType<FileConfigurationProvider>()));
            Assert.Equal(2, fileProvider.WatchCallCount);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(1, activeFailureProvider.Token.RegistrationCount);
            Assert.Equal("initial", manager["Key"]);
            Assert.All(failureSource.Providers, provider => Assert.Equal(0, provider.DisposeCount));

            failureSource.ClearFailure();
            fileProvider.SetContent(GetFileContent(sourceKind, "updated"));
            ((IConfigurationRoot)manager).Reload();
            Assert.Equal("updated", manager["Key"]);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);

            builder.Properties["retry"] = true;
            FileConfigurationProvider replacementProvider = Assert.Single(
                ((IConfigurationRoot)manager).Providers.OfType<FileConfigurationProvider>());
            Assert.NotSame(activeProvider, replacementProvider);
            Assert.Equal(3, fileProvider.WatchCallCount);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal("updated", manager["Key"]);

            manager.Dispose();
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Theory]
        [InlineData(FileSourceKind.Json)]
        [InlineData(FileSourceKind.Ini)]
        [InlineData(FileSourceKind.Xml)]
        public void FailedDirectRootConstructionDoesNotDisposeSuppliedFileProvider(FileSourceKind sourceKind)
        {
            var fileProvider = new TrackingFileProvider();
            FileConfigurationSource source = CreateFileSource(sourceKind, fileProvider, "missing");
            IConfigurationProvider suppliedFileProvider = source.Build(new ConfigurationBuilder());
            var registeredSource = new FaultingConfigurationSource();
            var failingSource = new FaultingConfigurationSource();
            var builder = new ConfigurationBuilder();
            var registeredProvider = (FaultingConfigurationProvider)registeredSource.Build(builder);
            var failingProvider = (FaultingConfigurationProvider)failingSource.Build(builder);
            InvalidOperationException failure = failingSource.FailAt(TestFailureStage.Load);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                new ConfigurationRoot(new[] { registeredProvider, suppliedFileProvider, failingProvider })));
            Assert.Equal(0, registeredProvider.Token.RegistrationCount);
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
            Assert.Equal(0, registeredProvider.DisposeCount);
            Assert.Equal(0, failingProvider.DisposeCount);

            ((IDisposable)suppliedFileProvider).Dispose();
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Theory]
        [InlineData(FileSourceKind.Json, false)]
        [InlineData(FileSourceKind.Ini, false)]
        [InlineData(FileSourceKind.Xml, false)]
        [InlineData(FileSourceKind.Json, true)]
        [InlineData(FileSourceKind.Ini, true)]
        [InlineData(FileSourceKind.Xml, true)]
        public void FailedBuildDoesNotDisposeCachedStandardFileProvider(
            FileSourceKind sourceKind,
            bool useDerivedSource)
        {
            var fileProvider = new TrackingFileProvider();
            fileProvider.SetContent(GetFileContent(sourceKind, "initial"));
            FileConfigurationSource providerSource = CreateFileSource(sourceKind, fileProvider, "settings", optional: false);
            IConfigurationProvider cachedProvider = providerSource.Build(new ConfigurationBuilder());
            var liveRoot = (ConfigurationRoot)new ConfigurationBuilder()
                .Add(new CachedProviderSource(cachedProvider))
                .Build();
            IConfigurationSource cachedSource = CreateCachedProviderSource(sourceKind, cachedProvider, useDerivedSource);
            var failureSource = new FaultingConfigurationSource();
            InvalidOperationException failure = failureSource.FailAt(TestFailureStage.Build);
            var failingBuilder = new ConfigurationBuilder();
            failingBuilder.Add(cachedSource);
            failingBuilder.Add(failureSource);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => failingBuilder.Build()));
            Assert.Equal(1, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
            Assert.Equal("initial", liveRoot["Key"]);

            fileProvider.SetContent(GetFileContent(sourceKind, "updated"));
            liveRoot.Reload();
            Assert.Equal("updated", liveRoot["Key"]);

            liveRoot.Dispose();
            Assert.Equal(0, fileProvider.LiveCallbackRegistrationCount);
            Assert.Equal(0, fileProvider.DisposeCount);
        }

        [Fact]
        public void LoadDataErrorRaisesOnLoadException()
        {
            const string FileName = $"{nameof(LoadDataErrorRaisesOnLoadException)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ");

            FileConfigurationProvider failingProvider = null;
            Exception failureException = null;
            Action<FileLoadExceptionContext> jsonLoadError = c =>
            {
                failureException = c.Exception;
                failingProvider = c.Provider;
                c.Ignore = true;
            };

            IConfigurationRoot cfgRoot = CreateBuilder()
                .AddJsonFile(s =>
                {
                    s.Path = FileName;
                    s.OnLoadException = jsonLoadError;
                })
                .Build();
            using IDisposable cfgRootDisposable = cfgRoot as IDisposable;

            Assert.NotNull(failingProvider);
            Assert.IsType<InvalidDataException>(failureException);
            Assert.Null(cfgRoot["JsonKey1"]);
        }

        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void LoadIoErrorRaisesOnLoadException()
        {
            const string FileName = $"{nameof(LoadIoErrorRaisesOnLoadException)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ""JsonValue1"" }");

            using (_fileSystem.LockFileReading(FileName))
            {
                FileConfigurationProvider failingProvider = null;
                Exception failureException = null;
                Action<FileLoadExceptionContext> jsonLoadError = c =>
                {
                    failureException = c.Exception;
                    failingProvider = c.Provider;
                    c.Ignore = true;
                };

                IConfigurationRoot cfgRoot = CreateBuilder()
                    .AddJsonFile(s =>
                    {
                        s.Path = FileName;
                        s.OnLoadException = jsonLoadError;
                    })
                    .Build();
                using IDisposable cfgRootDisposable = cfgRoot as IDisposable;

                Assert.NotNull(failingProvider);
                Assert.IsType<IOException>(failureException);
                Assert.Null(cfgRoot["JsonKey1"]);
            }
        }

        [Fact]
        public async Task ReloadDataErrorRaisesOnLoadException()
        {
            const string FileName = $"{nameof(ReloadDataErrorRaisesOnLoadException)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ""JsonValue1"" }");

            FileConfigurationProvider failingProvider = null;
            Exception failureException = null;
            Action<FileLoadExceptionContext> jsonLoadError = c =>
            {
                failureException = c.Exception;
                failingProvider = c.Provider;
                c.Ignore = true;
            };

            IConfigurationRoot cfgRoot = CreateBuilder()
                .AddJsonFile(s =>
                {
                    s.Path = FileName;
                    s.OnLoadException = jsonLoadError;
                    s.ReloadOnChange = true;
                })
                .Build();
            using IDisposable cfgRootDisposable = cfgRoot as IDisposable;
            IChangeToken reloadToken = cfgRoot.GetReloadToken();

            // No error should be triggered so far.
            Assert.Null(failingProvider);
            Assert.Null(failureException);
            Assert.Equal("JsonValue1", cfgRoot["JsonKey1"]);
            Assert.False(reloadToken.HasChanged);

            await WriteFilesForReload(
                cfgRoot,
                () => _fileSystem.WriteFile(FileName, @"{""JsonKey1"": "),
                Path.Combine(_fileSystem.RootPath, FileName));

            await WaitForChange(() => failingProvider != null, "File change did not raise OnLoadException event in time.");

            Assert.IsType<InvalidDataException>(failureException);

            // Check that value was removed from config
            Assert.Null(cfgRoot["JsonKey1"]);
            Assert.True(reloadToken.HasChanged);
        }

        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        public async Task ReloadIoErrorRaisesOnLoadException()
        {
            const string FileName = $"{nameof(ReloadIoErrorRaisesOnLoadException)}.json";

            _fileSystem.WriteFile(FileName, @"{""JsonKey1"": ""JsonValue1"" }");

            FileConfigurationProvider failingProvider = null;
            Exception failureException = null;
            Action<FileLoadExceptionContext> jsonLoadError = c =>
            {
                failureException = c.Exception;
                failingProvider = c.Provider;
                c.Ignore = true;
            };

            IConfigurationRoot cfgRoot = CreateBuilder()
                .AddJsonFile(s =>
                {
                    s.Path = FileName;
                    s.OnLoadException = jsonLoadError;
                    s.ReloadOnChange = true;
                })
                .Build();
            using IDisposable cfgRootDisposable = cfgRoot as IDisposable;
            IChangeToken reloadToken = cfgRoot.GetReloadToken();

            // No error should be triggered so far.
            Assert.Null(failingProvider);
            Assert.Null(failureException);
            Assert.Equal("JsonValue1", cfgRoot["JsonKey1"]);
            Assert.False(reloadToken.HasChanged);

            using (_fileSystem.LockFileReading(FileName))
            {
                await WriteFilesForReload(
                    cfgRoot,
                    // we need NoWait because Wait reads file under the hood and that is restricted in LockFileReading context
                    () => _fileSystem.WriteFileNoWait(FileName, @"{""JsonKey1"": ""JsonValue1Updated"" }"),
                    Path.Combine(_fileSystem.RootPath, FileName));

                await WaitForChange(() => failingProvider != null, "File change did not raise OnLoadException event in time.");
                Assert.IsType<IOException>(failureException);

                // IO error on reload does not invalidate existing config, yet value is not updated
                Assert.Equal("JsonValue1", cfgRoot["JsonKey1"]);
                Assert.False(reloadToken.HasChanged);
            }
        }

        [Fact]
        public async Task TouchingFileWillReload()
        {
            _fileSystem.WriteFile("reload.json", @"{""JsonKey1"": ""JsonValue1""}");
            _fileSystem.WriteFile("reload.ini", @"IniKey1 = IniValue1");
            _fileSystem.WriteFile("reload.xml", @"<settings XmlKey1=""XmlValue1""/>");

            var config = CreateBuilder()
                .AddIniFile("reload.ini", optional: false, reloadOnChange: true)
                .AddJsonFile("reload.json", optional: false, reloadOnChange: true)
                .AddXmlFile("reload.xml", optional: false, reloadOnChange: true)
                .Build();

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("XmlValue1", config["XmlKey1"]);

            var token = config.GetReloadToken();

            // Update files
            await WriteFilesForReload(
                config,
                () =>
                {
                    _fileSystem.WriteFile("reload.json", @"{""JsonKey1"": ""JsonValue2""}");
                    _fileSystem.WriteFile("reload.ini", @"IniKey1 = IniValue2");
                    _fileSystem.WriteFile("reload.xml", @"<settings XmlKey1=""XmlValue2""/>");
                },
                Path.Combine(_fileSystem.RootPath, "reload.json"),
                Path.Combine(_fileSystem.RootPath, "reload.ini"),
                Path.Combine(_fileSystem.RootPath, "reload.xml"));

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue2"
                    && config["IniKey1"] == "IniValue2"
                    && config["XmlKey1"] == "XmlValue2",
                "Reload failed after touching files.");

            Assert.Equal("JsonValue2", config["JsonKey1"]);
            Assert.Equal("IniValue2", config["IniKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey1"]);
            Assert.True(token.HasChanged);
        }

        [Fact]
        public async Task CreatingOptionalFileInNonExistentDirectoryWillReload()
        {
            var directory = Path.GetRandomFileName();
            var jsonRootRelativeFile = Path.Combine(directory, Path.GetRandomFileName());
            var jsonAbsoluteFile = Path.Combine(_fileSystem.RootPath, jsonRootRelativeFile);

            var config = new ConfigurationBuilder()
                .AddJsonFile(jsonAbsoluteFile, optional: true, reloadOnChange: true)
                .Build();

            Assert.Null(config["JsonKey1"]);

            var createToken = config.GetReloadToken();
            Assert.False(createToken.HasChanged);

            _fileSystem.CreateFolder(directory);

            await Task.Delay(2000);

            _fileSystem.WriteFile(jsonRootRelativeFile, @"{""JsonKey1"": ""JsonValue1""}");

            await Task.Delay(2500);

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue1",
                "Notification failed for file when it did not previously exist.",
                multiplier: 4);

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.True(createToken.HasChanged);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeletingFilesThatRedefineKeysWithReload(bool optional)
        {
            _fileSystem.WriteFile(_jsonFile, @"{""Key"": ""JsonValue1""}");
            _fileSystem.WriteFile(_iniFile, @"Key = IniValue1");
            _fileSystem.WriteFile(_xmlFile, @"<settings Key=""XmlValue1""/>");

            var config = CreateBuilder()
                .AddXmlFile(_xmlFile, optional, reloadOnChange: true)
                .AddJsonFile(_jsonFile, optional, reloadOnChange: true)
                .AddIniFile(_iniFile, optional, reloadOnChange: true)
                .Build();

            Assert.Equal("IniValue1", config["Key"]);

            // Delete files and ensure order is preserved
            var token = config.GetReloadToken();
            _fileSystem.DeleteFile(_iniFile);

            await WaitForChange(
                () => config["Key"] == "JsonValue1",
                "Notification failed for deleting ini file.");

            Assert.Equal("JsonValue1", config["Key"]);
            Assert.True(token.HasChanged);

            token = config.GetReloadToken();
            _fileSystem.DeleteFile(_jsonFile);

            await WaitForChange(
                () => config["Key"] == "XmlValue1",
                "Notification failed for deleting JSON file.");

            Assert.Equal("XmlValue1", config["Key"]);
            Assert.True(token.HasChanged);

            token = config.GetReloadToken();
            _fileSystem.DeleteFile(_xmlFile);

            await WaitForChange(
                () => config["Key"] == null,
                "Notification failed for deleting XML file.");

            Assert.Null(config["Key"]);
            Assert.True(token.HasChanged);

            token = config.GetReloadToken();
            _fileSystem.WriteFile(_jsonFile, @"{""Key"": ""JsonValue1""}");

            await WaitForChange(
                () => config["Key"] == "JsonValue1",
                "Notification failed for re-creating JSON file.");

            Assert.Equal("JsonValue1", config["Key"]);
            Assert.True(token.HasChanged);

            // Adding a file earlier in the chain has no effect
            token = config.GetReloadToken();
            _fileSystem.WriteFile(_xmlFile, @"<settings Key=""XmlValue1""/>");

            await WaitForChange(
                () => token.HasChanged,
                "Notification failed for re-creating XML file.");

            Assert.Equal("JsonValue1", config["Key"]);
            Assert.True(token.HasChanged);

            token = config.GetReloadToken();
            _fileSystem.WriteFile(_iniFile, @"Key = IniValue1");

            await WaitForChange(
                () => config["Key"] == "IniValue1",
                "Notification failed for re-creating ini file.");

            Assert.Equal("IniValue1", config["Key"]);
            Assert.True(token.HasChanged);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeletingFileWillReload(bool optional)
        {
            _fileSystem.WriteFile(_jsonFile, @"{""JsonKey1"": ""JsonValue1""}");
            _fileSystem.WriteFile(_iniFile, @"IniKey1 = IniValue1");
            _fileSystem.WriteFile(_xmlFile, @"<settings XmlKey1=""XmlValue1""/>");

            var config = CreateBuilder()
                .AddXmlFile(_xmlFile, optional, reloadOnChange: true)
                .AddJsonFile(_jsonFile, optional, reloadOnChange: true)
                .AddIniFile(_iniFile, optional, reloadOnChange: true)
                .Build();

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("XmlValue1", config["XmlKey1"]);

            var token = config.GetReloadToken();

            // Delete files
            _fileSystem.DeleteFile(_jsonFile);
            _fileSystem.DeleteFile(_iniFile);
            _fileSystem.DeleteFile(_xmlFile);

            await WaitForChange(
                () => config["JsonKey1"] == null
                    && config["IniKey1"] == null
                    && config["XmlKey1"] == null,
                "Reload failed after deleting files.");

            Assert.Null(config["JsonKey1"]);
            Assert.Null(config["IniKey1"]);
            Assert.Null(config["XmlKey1"]);
            Assert.True(token.HasChanged);
        }

        [Fact]
        public async Task CreatingWritingDeletingCreatingFileWillReload()
        {
            var config = CreateBuilder()
                .AddIniFile(_iniFile, optional: true, reloadOnChange: true)
                .AddJsonFile(_jsonFile, optional: true, reloadOnChange: true)
                .AddXmlFile(_xmlFile, optional: true, reloadOnChange: true)
                .Build();

            Assert.Null(config["JsonKey1"]);
            Assert.Null(config["IniKey1"]);
            Assert.Null(config["XmlKey1"]);

            var createToken = config.GetReloadToken();

            _fileSystem.WriteFile(_jsonFile, @"{""JsonKey1"": ""JsonValue1""}");
            _fileSystem.WriteFile(_iniFile, @"IniKey1 = IniValue1");
            _fileSystem.WriteFile(_xmlFile, @"<settings XmlKey1=""XmlValue1""/>");

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue1"
                    && config["IniKey1"] == "IniValue1"
                    && config["XmlKey1"] == "XmlValue1",
                "Reload failed after files created.");

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.True(createToken.HasChanged);

            var writeToken = config.GetReloadToken();

            await WriteFilesForReload(
                config,
                () =>
                {
                    _fileSystem.WriteFile(_jsonFile, @"{""JsonKey1"": ""JsonValue2""}");
                    _fileSystem.WriteFile(_iniFile, @"IniKey1 = IniValue2");
                    _fileSystem.WriteFile(_xmlFile, @"<settings XmlKey1=""XmlValue2""/>");
                },
                Path.Combine(_fileSystem.RootPath, _jsonFile),
                Path.Combine(_fileSystem.RootPath, _iniFile),
                Path.Combine(_fileSystem.RootPath, _xmlFile));

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue2"
                    && config["IniKey1"] == "IniValue2"
                    && config["XmlKey1"] == "XmlValue2",
                "Reload failed after files changed after creation.");

            Assert.Equal("JsonValue2", config["JsonKey1"]);
            Assert.Equal("IniValue2", config["IniKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey1"]);
            Assert.True(writeToken.HasChanged);

            var deleteToken = config.GetReloadToken();

            // Delete files
            _fileSystem.DeleteFile(_jsonFile);
            _fileSystem.DeleteFile(_iniFile);
            _fileSystem.DeleteFile(_xmlFile);

            await WaitForChange(
                () => config["JsonKey1"] == null
                    && config["IniKey1"] == null
                    && config["XmlKey1"] == null,
                "Reload failed after deleted after creation.");

            Assert.Null(config["JsonKey1"]);
            Assert.Null(config["IniKey1"]);
            Assert.Null(config["XmlKey1"]);
            Assert.True(deleteToken.HasChanged);

            var createAgainToken = config.GetReloadToken();

            _fileSystem.WriteFile(_jsonFile, @"{""JsonKey1"": ""JsonValue1""}");
            _fileSystem.WriteFile(_iniFile, @"IniKey1 = IniValue1");
            _fileSystem.WriteFile(_xmlFile, @"<settings XmlKey1=""XmlValue1""/>");

            await WaitForChange(
                () => config["JsonKey1"] == "JsonValue1"
                    && config["IniKey1"] == "IniValue1"
                    && config["XmlKey1"] == "XmlValue1",
                "Reload failed after create-delete-create.");

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("IniValue1", config["IniKey1"]);
            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.True(createAgainToken.HasChanged);
        }

        [Fact]
        public void LoadIncorrectJsonFile_ThrowException()
        {
            var json = @"{
                'name': 'test',
                'address': {
                    'street': 'Something street' /*Missing comma*/
                    'zipcode': '12345'
                }
            }";
            _fileSystem.WriteFile(_jsonFile, json);

            var exception = Assert.Throws<InvalidDataException>(() => CreateBuilder().AddJsonFile(_jsonFile).Build());
            Assert.Contains("Could not parse the JSON file.", exception.InnerException.Message);
        }

        [Fact]
        public void SetBasePathCalledMultipleTimesForEachSourceLastOneWins()
        {
            var builder = new ConfigurationBuilder();

            var jsonConfigFilePath = "test.json";
            _fileSystem.CreateFolder("NewBase");
            _fileSystem.WriteFile(Path.Combine("NewBase", jsonConfigFilePath), _jsonConfigFileContent);

            var xmlConfigFilePath = "test.xml";
            _fileSystem.WriteFile(Path.Combine("NewBase", xmlConfigFilePath), _xmlConfigFileContent);

            builder.AddXmlFile("test.xml")
                .SetBasePath(Path.Combine(_fileSystem.RootPath, "NewBase"))
                .AddJsonFile("test.json");

            var config = builder.Build();

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("JsonValue2", config["Json.Key2:JsonKey3"]);
            Assert.Equal("JsonValue3", config["Json.Key2:Json.Key4"]);
            Assert.Equal("JsonValue4", config["Json.Key2:JsonKey5:JsonKey6"]);
            Assert.Equal("JsonValue5", config["CommonKey1:CommonKey2:JsonKey7"]);

            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey2:XmlKey3"]);
            Assert.Equal("XmlValue3", config["XmlKey2:XmlKey4"]);
            Assert.Equal("XmlValue4", config["XmlKey2:XmlKey5:XmlKey6"]);

            _fileSystem.DeleteFile(Path.Combine("NewBase", jsonConfigFilePath));
            _fileSystem.DeleteFile(Path.Combine("NewBase", xmlConfigFilePath));
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/60583", TestPlatforms.iOS | TestPlatforms.tvOS)]
        public void GetDefaultBasePathForSources()
        {
            var builder = new ConfigurationBuilder();

            var jsonConfigFilePath = Path.Combine(_basePath, "test.json");
            var xmlConfigFilePath = Path.Combine(_basePath, "xmltest.xml");
            _fileSystem.WriteFile(jsonConfigFilePath, _jsonConfigFileContent, absolute: true);
            _fileSystem.WriteFile(xmlConfigFilePath, _xmlConfigFileContent, absolute: true);

            builder.AddJsonFile("test.json").AddXmlFile("xmltest.xml");

            var config = builder.Build();

            Assert.Equal("JsonValue1", config["JsonKey1"]);
            Assert.Equal("JsonValue2", config["Json.Key2:JsonKey3"]);
            Assert.Equal("JsonValue3", config["Json.Key2:Json.Key4"]);
            Assert.Equal("JsonValue4", config["Json.Key2:JsonKey5:JsonKey6"]);
            Assert.Equal("JsonValue5", config["CommonKey1:CommonKey2:JsonKey7"]);

            Assert.Equal("XmlValue1", config["XmlKey1"]);
            Assert.Equal("XmlValue2", config["XmlKey2:XmlKey3"]);
            Assert.Equal("XmlValue3", config["XmlKey2:XmlKey4"]);
            Assert.Equal("XmlValue4", config["XmlKey2:XmlKey5:XmlKey6"]);

            _fileSystem.DeleteFile(jsonConfigFilePath, absolute: true);
            _fileSystem.DeleteFile(xmlConfigFilePath, absolute: true);
        }

        [Fact]
        [SkipOnPlatform(TestPlatforms.Browser | TestPlatforms.iOS | TestPlatforms.tvOS, "System.IO.FileSystem.Watcher is not supported on Browser/iOS/tvOS")]
        public void CanEnumerateProviders()
        {
            var config = CreateBuilder()
                .AddIniFile(_iniFile, optional: true, reloadOnChange: true)
                .AddJsonFile(_jsonFile, optional: true, reloadOnChange: true)
                .AddXmlFile(_xmlFile, optional: true, reloadOnChange: true)
                .Build();

            var providers = config.Providers;
            Assert.Equal(3, providers.Count());
            Assert.NotNull(providers.Single(p => p is JsonConfigurationProvider));
            Assert.NotNull(providers.Single(p => p is XmlConfigurationProvider));
            Assert.NotNull(providers.Single(p => p is IniConfigurationProvider));
        }

        [Fact]
        public async Task TouchingFileWillReloadForUserSecrets()
        {
            // A unique id keeps concurrently running assemblies from sharing the same secrets folder.
            string userSecretsId = $"Test_{Guid.NewGuid():N}";
            var userSecretsPath = PathHelper.GetSecretsPathFromSecretsId(userSecretsId);
            var userSecretsFolder = Path.GetDirectoryName(userSecretsPath);

            // The secrets path is absolute and lives under the real user profile, so DisposableFileSystem
            // writes straight through it and cannot clean it up for us.
            IConfigurationRoot config = null;

            try
            {
                _fileSystem.CreateFolder(userSecretsFolder);
                _fileSystem.WriteFile(userSecretsPath, @"{""UserSecretKey1"": ""UserSecretValue1""}");

                config = CreateBuilder()
                    .AddUserSecrets(userSecretsId, reloadOnChange: true)
                    .Build();

                Assert.Equal("UserSecretValue1", config["UserSecretKey1"]);

                var token = config.GetReloadToken();

                // Update file
                await WriteFilesForReload(
                    config,
                    () => _fileSystem.WriteFile(userSecretsPath, @"{""UserSecretKey1"": ""UserSecretValue2""}"),
                    userSecretsPath);

                await WaitForChange(
                    () => config["UserSecretKey1"] == "UserSecretValue2",
                    "Reload failed after create-delete-create.");

                Assert.Equal("UserSecretValue2", config["UserSecretKey1"]);
                Assert.True(token.HasChanged);
            }
            finally
            {
                // Stop watching before removing the folder the watcher is pointed at.
                (config as IDisposable)?.Dispose();

                try
                {
                    Directory.Delete(userSecretsFolder, true);
                }
                catch
                {
                    // Don't throw if this fails.
                }
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        public void BindingDoesNotThrowIfReloadedDuringBinding()
        {
            WriteTestFiles();

            var configurationBuilder = CreateBuilder();
            configurationBuilder.Add(new TestIniSourceProvider(_iniFile));
            configurationBuilder.Add(new TestJsonSourceProvider(_jsonFile));
            configurationBuilder.Add(new TestXmlSourceProvider(_xmlFile));
            configurationBuilder.AddEnvironmentVariables();
            configurationBuilder.AddCommandLine(new[] { "--CmdKey1=CmdValue1" });
            configurationBuilder.AddInMemoryCollection(_memConfigContent);

            var config = configurationBuilder.Build();

            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                void ReloadLoop()
                {
                    while (!cts.IsCancellationRequested)
                    {
                        config.Reload();
                    }
                }

                _ = Task.Run(ReloadLoop);

                MyOptions options;

                do
                {
                    options = config.Get<MyOptions>();
                }
                while (!cts.IsCancellationRequested);

                Assert.Equal("CmdValue1", options.CmdKey1);
                Assert.Equal("IniValue1", options.IniKey1);
                Assert.Equal("JsonValue1", options.JsonKey1);
                Assert.Equal("MemValue1", options.MemKey1);
                Assert.Equal("XmlValue1", options.XmlKey1);
            }
        }

        public void Dispose()
        {
            _fileProvider.Dispose();
            _fileSystem.Dispose();
        }

        private async Task WaitForChange(
            Func<bool> test,
            string failureMessage,
            int multiplier = 1)
        {
            var i = 0;
            while (!test())
            {
                if (++i >= _retries * multiplier)
                {
                    throw new Exception(failureMessage);
                }

                await Task.Delay(_msDelay);
            }
        }

        // UseActivePolling is the reliable post-initialization indicator, not UsePollingFileWatcher,
        // until https://github.com/dotnet/runtime/issues/131871 is fixed.
        private static bool UsesPolling(IConfigurationRoot config) =>
            config.Providers
                .OfType<FileConfigurationProvider>()
                .Any(provider => provider.Source.FileProvider is PhysicalFileProvider { UseActivePolling: true });

        private async Task WriteFilesForReload(IConfigurationRoot config, Action writeFiles, params string[] filePaths)
        {
            DateTime[] previousWriteTimes = null;

            if (UsesPolling(config))
            {
                // Polling detects overwrites by timestamp, so ensure the new write gets a different timestamp.
                previousWriteTimes = filePaths.Select(File.GetLastWriteTimeUtc).ToArray();
                DateTime latestWriteTime = previousWriteTimes.Max();
                TimeSpan delay = latestWriteTime.AddSeconds(1) - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }

            writeFiles();

            if (previousWriteTimes is not null)
            {
                for (int i = 0; i < filePaths.Length; i++)
                {
                    Assert.NotEqual(previousWriteTimes[i], File.GetLastWriteTimeUtc(filePaths[i]));
                }
            }
        }

        public enum FileSourceKind
        {
            Json,
            Ini,
            Xml
        }

        public enum TestFailureStage
        {
            Build,
            Load,
            GetReloadToken,
            RegisterChangeCallback
        }

        public static IEnumerable<object[]> FileSourceFailureStages()
        {
            foreach (FileSourceKind sourceKind in new[] { FileSourceKind.Json, FileSourceKind.Ini, FileSourceKind.Xml })
            {
                foreach (TestFailureStage failureStage in new[]
                {
                    TestFailureStage.Build,
                    TestFailureStage.Load,
                    TestFailureStage.GetReloadToken,
                    TestFailureStage.RegisterChangeCallback
                })
                {
                    yield return new object[] { sourceKind, failureStage };
                }
            }
        }

        private static FileConfigurationSource CreateFileSource(
            FileSourceKind sourceKind,
            TrackingFileProvider fileProvider,
            string path,
            bool optional = true)
        {
            FileConfigurationSource source = sourceKind switch
            {
                FileSourceKind.Json => new JsonConfigurationSource(),
                FileSourceKind.Ini => new IniConfigurationSource(),
                FileSourceKind.Xml => new XmlConfigurationSource(),
                _ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
            };

            source.FileProvider = fileProvider;
            source.Path = path;
            source.Optional = optional;
            source.ReloadOnChange = true;
            return source;
        }

        private static IConfigurationSource CreateCachedProviderSource(
            FileSourceKind sourceKind,
            IConfigurationProvider provider,
            bool useDerivedSource)
        {
            if (!useDerivedSource)
            {
                return new CachedProviderSource(provider);
            }

            return sourceKind switch
            {
                FileSourceKind.Json => new CachedJsonConfigurationSource(provider),
                FileSourceKind.Ini => new CachedIniConfigurationSource(provider),
                FileSourceKind.Xml => new CachedXmlConfigurationSource(provider),
                _ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
            };
        }

        private static string GetFileContent(FileSourceKind sourceKind, string value) =>
            sourceKind switch
            {
                FileSourceKind.Json => $"{{\"Key\":\"{value}\"}}",
                FileSourceKind.Ini => $"Key={value}",
                FileSourceKind.Xml => $"<settings Key=\"{value}\" />",
                _ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
            };

        private sealed class TrackingFileProvider : IFileProvider, IDisposable
        {
            private byte[] _content;
            private int _disposeCount;
            private int _liveCallbackRegistrationCount;
            private int _watchCallCount;

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public int LiveCallbackRegistrationCount => Volatile.Read(ref _liveCallbackRegistrationCount);

            public int WatchCallCount => Volatile.Read(ref _watchCallCount);

            public void SetContent(string content) => _content = Encoding.UTF8.GetBytes(content);

            public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

            public IFileInfo GetFileInfo(string subpath) =>
                _content is null ? new NotFoundFileInfo(subpath) : new MemoryFileInfo(subpath, _content);

            public IChangeToken Watch(string filter)
            {
                Interlocked.Increment(ref _watchCallCount);
                return new TrackingChangeToken(this);
            }

            public void Dispose() => Interlocked.Increment(ref _disposeCount);

            private void RegisterCallback() => Interlocked.Increment(ref _liveCallbackRegistrationCount);

            private void UnregisterCallback() => Interlocked.Decrement(ref _liveCallbackRegistrationCount);

            private sealed class TrackingChangeToken : IChangeToken
            {
                private readonly TrackingFileProvider _owner;

                public TrackingChangeToken(TrackingFileProvider owner)
                {
                    _owner = owner;
                }

                public bool ActiveChangeCallbacks => true;

                public bool HasChanged => false;

                public IDisposable RegisterChangeCallback(Action<object> callback, object state)
                {
                    _owner.RegisterCallback();
                    return new CallbackRegistration(_owner.UnregisterCallback);
                }
            }
        }

        private sealed class MemoryFileInfo : IFileInfo
        {
            private readonly byte[] _content;

            public MemoryFileInfo(string name, byte[] content)
            {
                Name = name;
                _content = content;
            }

            public bool Exists => true;

            public bool IsDirectory => false;

            public DateTimeOffset LastModified => default;

            public long Length => _content.Length;

            public string Name { get; }

            public string PhysicalPath => null;

            public Stream CreateReadStream() => new MemoryStream(_content, writable: false);
        }

        private sealed class FaultingConfigurationSource : IConfigurationSource
        {
            private readonly List<FaultingConfigurationProvider> _providers = new();

            public IReadOnlyList<FaultingConfigurationProvider> Providers => _providers;

            public TestFailureStage? FailureStage { get; private set; }

            public InvalidOperationException Failure { get; } =
                new InvalidOperationException("Configuration initialisation failed.");

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                if (FailureStage is TestFailureStage.Build)
                {
                    throw Failure;
                }

                var provider = new FaultingConfigurationProvider(this);
                _providers.Add(provider);
                return provider;
            }

            public InvalidOperationException FailAt(TestFailureStage failureStage)
            {
                FailureStage = failureStage;
                return Failure;
            }

            public void ClearFailure() => FailureStage = null;
        }

        private sealed class FaultingConfigurationProvider : ConfigurationProvider, IConfigurationProvider, IDisposable
        {
            private readonly FaultingConfigurationSource _source;

            public FaultingConfigurationProvider(FaultingConfigurationSource source)
            {
                _source = source;
                Token = new FaultingChangeToken(source);
                Set("FaultingProvider", "value");
            }

            public int DisposeCount { get; private set; }

            public FaultingChangeToken Token { get; }

            public override void Load()
            {
                if (_source.FailureStage is TestFailureStage.Load)
                {
                    throw _source.Failure;
                }
            }

            IChangeToken IConfigurationProvider.GetReloadToken()
            {
                if (_source.FailureStage is TestFailureStage.GetReloadToken)
                {
                    throw _source.Failure;
                }

                return Token;
            }

            public void Dispose() => DisposeCount++;
        }

        private sealed class FaultingChangeToken : IChangeToken
        {
            private readonly FaultingConfigurationSource _source;

            public FaultingChangeToken(FaultingConfigurationSource source)
            {
                _source = source;
            }

            public int RegistrationCount { get; private set; }

            public bool ActiveChangeCallbacks => true;

            public bool HasChanged => false;

            public IDisposable RegisterChangeCallback(Action<object> callback, object state)
            {
                if (_source.FailureStage is TestFailureStage.RegisterChangeCallback)
                {
                    throw _source.Failure;
                }

                RegistrationCount++;
                return new CallbackRegistration(Unregister);
            }

            private void Unregister() => RegistrationCount--;
        }

        private sealed class CallbackRegistration : IDisposable
        {
            private Action _dispose;

            public CallbackRegistration(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }

        private sealed class CachedProviderSource : IConfigurationSource
        {
            private readonly IConfigurationProvider _provider;

            public CachedProviderSource(IConfigurationProvider provider)
            {
                _provider = provider;
            }

            public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
        }

        private sealed class CachedJsonConfigurationSource : JsonConfigurationSource
        {
            private readonly IConfigurationProvider _provider;

            public CachedJsonConfigurationSource(IConfigurationProvider provider)
            {
                _provider = provider;
            }

            public override IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
        }

        private sealed class CachedIniConfigurationSource : IniConfigurationSource
        {
            private readonly IConfigurationProvider _provider;

            public CachedIniConfigurationSource(IConfigurationProvider provider)
            {
                _provider = provider;
            }

            public override IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
        }

        private sealed class CachedXmlConfigurationSource : XmlConfigurationSource
        {
            private readonly IConfigurationProvider _provider;

            public CachedXmlConfigurationSource(IConfigurationProvider provider)
            {
                _provider = provider;
            }

            public override IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
        }

        private sealed class MyOptions
        {
            public string CmdKey1 { get; set; }

            public string IniKey1 { get; set; }

            public string JsonKey1 { get; set; }

            public string MemKey1 { get; set; }

            public string XmlKey1 { get; set; }
        }

        private async Task WatchOverConfigJsonFileAndUpdateIt(string relativePathPrefix)
        {
            // Use a unique file name so that repeated or concurrent runs sharing the same output
            // directory cannot observe each other's files.
            string fileName = $"testFileToReload_{Guid.NewGuid():N}.json";

            // The prefix is concatenated rather than combined on purpose: preserving the unnormalized
            // "./" or ".\" is the whole point of the test, and Path.Combine would strip it.
            string configuredPath = relativePathPrefix + fileName;

            // A relative path is resolved against the default file provider root (AppContext.BaseDirectory)
            // rather than the current working directory, so the file has to be written there for the
            // watcher to see it.
            string watchedFilePath = Path.Combine(_basePath, fileName);

            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile(configuredPath, optional: true, reloadOnChange: true)
                .Build();

            IFileProvider defaultFileProvider = config.Providers
                .OfType<FileConfigurationProvider>()
                .Single()
                .Source.FileProvider;

            try
            {
                var reloadedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using IDisposable subscription = ChangeToken.OnChange(config.GetReloadToken, () => reloadedSignal.TrySetResult(true));

                File.WriteAllText(watchedFilePath, @"{""Prop2"": ""Value2""}");
                await Task.WhenAny(reloadedSignal.Task, Task.Delay(s_maxWaitForReload));
                Assert.True(reloadedSignal.Task.IsCompleted, "on file change event handler did not get executed");
                Assert.Equal("Value2", config["Prop2"]);
            }
            finally
            {
                (config as IDisposable)?.Dispose();
                (defaultFileProvider as IDisposable)?.Dispose();

                try
                {
                    File.Delete(watchedFilePath);
                }
                catch
                {
                    // Don't throw if this fails; a cleanup error must not mask an assertion failure.
                }
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsWindows))]
        public async Task OnChangeGetFiredForRelativeWindowsPath()
        {
            await WatchOverConfigJsonFileAndUpdateIt(".\\");
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsLinux))]
        public async Task OnChangeGetFiredForRelativeLinuxPath()
        {
            await WatchOverConfigJsonFileAndUpdateIt("./");
        }
    }
}
