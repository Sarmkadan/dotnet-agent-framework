# Contributing

Thanks for your interest. This is a small project; the process is deliberately light.

## Setup

```bash
git clone https://github.com/Sarmkadan/dotnet-agent-framework.git
cd dotnet-agent-framework
dotnet build
dotnet test
```

Requires the .NET 9 SDK or later. If you only have the .NET 10 runtime installed, run tests with `DOTNET_ROLL_FORWARD=Major dotnet test`.

## Making changes

1. Open an issue first for anything beyond a small fix, so we can agree on the approach.
2. Fork, branch from `main`, make the change.
3. Add or update tests. Bug fixes should include a failing test that the fix turns green.
4. Every public member needs XML docs; `dotnet build` fails without them.
5. Run `dotnet format` and make sure `dotnet build` produces zero warnings (they are errors in CI).
6. Open a pull request. Describe what changed and why; link the issue.

## Commit messages

Conventional commits: `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`. Keep the subject under 72 characters.

## Design constraints

- The core package depends on `System.Text.Json` only. Provider adapters (OpenAI, Anthropic, ...) go in separate packages.
- Prefer adding an implementation of an existing interface over adding a new interface.
- Tools return recoverable errors as text so the model can react; exceptions are for programmer errors.

## Code of conduct

Be respectful and constructive. Disagreement about design is welcome; personal attacks are not.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
