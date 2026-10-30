# Microsoft.Extensions.Configuration

`Microsoft.Extensions.Configuration` is combined with a core configuration abstraction under `Microsoft.Extensions.Configuration.Abstractions` that allows for building different kinds of configuration providers to retrieve key/value pair configuration values from in the form of `IConfiguration`.

Documentation can be found at https://learn.microsoft.com/dotnet/core/extensions/configuration.

## Initialisation failures

Failed `ConfigurationBuilder.Build` and `ConfigurationManager` initialisation release acquired registrations and dispose fresh candidates from the exact built-in `JsonConfigurationSource`, `IniConfigurationSource` and `XmlConfigurationSource` types. Disposing these providers releases their own watch registrations, not the supplied `IFileProvider`. These sources create new providers on each build, so retrying does not reuse disposed candidates.

Custom sources, derived source types and chained configurations are excluded from automatic provider cleanup on failure. They can return a shared provider or own an existing configuration that another root still needs. A fresh chained wrapper does not imply exclusive ownership of the wrapped configuration. Successful construction and normal disposal retain their existing ownership behaviour.

Fresh candidates remain owned by their construction attempt until publication. Applications must not transfer them to another root from an initialisation callback; cross-root transfers during construction are not tracked. A manager can recognise and preserve candidates already published into its own provider set.

Failed direct `ConfigurationRoot` construction releases acquired registrations but never disposes the caller-supplied providers. Explicit reloads and provider-triggered notifications also leave active providers alone on failure.

If appending a source to `ConfigurationManager.Sources` fails before the provider is accepted, the appended source is removed and any acquired registration is released. To retry, correct the failure and explicitly add the source again. Previously, the failed source remained in the collection and could be retried automatically by an unrelated source or property edit. Reentrant edits made by a custom source or provider are not rolled back; an outer failure must not remove an entry that was moved or accepted by a nested operation.

If a reentrant edit moves the entry before failing, the outer source can remain registered without an active provider. Inspect and correct the source collection before retrying.

Provider cleanup during a failed multi-source build is conservative if the source collection was modified during the attempt: registrations are released, but providers are left alone because their original source associations can no longer be established safely.

Other source edits and builder-property edits remain visible if rebuilding fails. The active providers and their registrations are retained until replacement providers are ready. Correct the edit and trigger another rebuild to retry. This is not a rollback of provider data or of side effects performed by custom sources and providers.

Once replacement providers are accepted, exceptions from retiring the previous resources or notifying subscribers do not undo the accepted configuration. Subscribers are notified even if retirement throws. Cleanup attempts all acquired registrations; if both initialisation and cleanup fail, an `AggregateException` contains the initial failure followed by the cleanup failures. If retirement and notification both fail, both exceptions are preserved in an `AggregateException`.

Use `AggregateException.Flatten()` to inspect individual failures when retirement or notification has already aggregated exceptions.

## Contribution Bar
- [x] [We consider new features, new APIs, bug fixes, and performance changes](../README.md#contribution-bar)

The APIs and functionality are mature, but do get extended occasionally.

## Deployment
[Microsoft.Extensions.Configuration](https://www.nuget.org/packages/Microsoft.Extensions.Configuration) is included in the ASP.NET Core shared framework. The package is deployed as out-of-band (OOB) too and can be referenced into projects directly.
