# Contributing to UE4Decompiler

Thank you for your interest in contributing to UE4Decompiler! We welcome bug reports, feature requests, documentation improvements, and code contributions.

---

## Development Setup

1. **Prerequisites**:
   - .NET 8.0 SDK or newer
   - Git
   - An IDE of your choice: Visual Studio 2022, JetBrains Rider, or VS Code with C# Dev Kit.

2. **Building the Solution**:
   ```bash
   dotnet build UE4Decompiler.sln
   ```

3. **Running the Tests**:
   ```bash
   dotnet test tests/UE4Decompiler.Tests/UE4Decompiler.Tests.csproj
   ```

---

## Contribution Guidelines

1. **Do Not Break Existing Reconstruction**:
   - All existing 4.21 package writing and reconstructor routing must remain working and tested.
2. **Add Unit Tests**:
   - Every new parser, reconstructor, or path utility should be accompanied by corresponding tests in `tests/UE4Decompiler.Tests/`.
3. **Cross-Platform Compatibility**:
   - Avoid Windows-only path separators, case-sensitivity assumptions, or platform-specific APIs in `UE4Decompiler.Core`.
4. **Code Style**:
   - Follow standard C# coding conventions and the repository `.editorconfig`.
5. **Security**:
   - Always route output file generation through `PathUtils.SafeCombine`.
