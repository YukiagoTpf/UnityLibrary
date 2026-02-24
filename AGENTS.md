# Repository Guidelines

## Project Structure & Module Organization
This repository has two top-level areas: project code in `Library/` and technical notes in `Document/`.
- `Library/` is the Unity project root (open this folder in Unity).
- `Library/Assets/Rendering/RenderFeature/` contains URP renderer features and effect modules (for example `FXAA/`).
- `Library/Assets/Rendering/Shader/` stores shared/template shaders.
- `Library/Assets/Scenes/` holds sample scenes; `Library/Assets/Settings/` contains URP assets and renderer settings.
- `Library/Packages/manifest.json` defines package dependencies; `Library/ProjectSettings/` tracks Unity/editor configuration.
- `Document/` contains implementation notes and design writeups.

## Build, Test, and Development Commands
Use Unity Editor `6000.3.6f1` for consistency.
- Open project: `"<UnityEditorPath>\\Unity.exe" -projectPath ".\\Library"`
- Run EditMode tests: `"<UnityEditorPath>\\Unity.exe" -batchmode -nographics -projectPath ".\\Library" -runTests -testPlatform EditMode -testResults ".\\TestResults\\EditMode.xml" -quit`
- Run PlayMode tests: `"<UnityEditorPath>\\Unity.exe" -batchmode -nographics -projectPath ".\\Library" -runTests -testPlatform PlayMode -testResults ".\\TestResults\\PlayMode.xml" -quit`
- Build player: use Unity Build Profiles in the Editor (no repository build script yet).

## Coding Style & Naming Conventions
- C#: 4-space indentation, braces on new lines, `PascalCase` for types/methods.
- Field prefixes follow existing code: private instance `m_`, static readonly `s_`, constants `k_`.
- Keep renderer feature naming consistent with current patterns, e.g., `RenderfeatureFXAA`, `FXAAVolume`.
- Keep shader and C# property names aligned (`_FxaaSubpix`, `_Intensity`, etc.).

## Testing Guidelines
- Unity Test Framework is available (`com.unity.test-framework`).
- Add tests under `Library/Assets/Tests/EditMode` and `Library/Assets/Tests/PlayMode`.
- Use file names like `RenderfeatureFXAATests.cs` and descriptive test method names.
- Run both EditMode and PlayMode tests before opening a PR.

## Commit & Pull Request Guidelines
Git history is minimal (`Initial commit`), so use a clear convention going forward.
- Commit format: `type(scope): summary` (example: `feat(rendering): add FXAA volume toggles`).
- Keep commits focused; avoid mixing rendering logic and documentation refactors.
- PRs should include: purpose, key file paths changed, test evidence, and screenshots/GIFs for visual rendering changes.
- Link related issues/tasks and note the Unity version used for validation.
