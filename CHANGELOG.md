# Release History

## [Unreleased]

### Features Added

- Core exposes general RGF initialization and a capability response without AI runtime dependencies. The response includes Recroby enablement and the Legacy setting that controls visibility of the Blazor Ignore case checkbox. Recroby integration is enabled through its own package and `AddRGFWithRecroby()`.

### Other Changes

- `RgfCore<TEntity>` remains available for existing Entity classes and now emits an obsolete warning. New Entity classes can inherit from `RgBase<TEntity>` in the `RecroGrid` package.
