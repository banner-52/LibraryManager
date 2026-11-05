# LibraryManager

Clean WPF rebuild of the previous software package management application.

## Project rule
All repository content must be written in English, including code, comments, identifiers, UI text, scripts, logs, and documentation.

## Architecture
- Views / XAML: visual structure.
- Views / XAML.cs: UI-specific behavior.
- Services: application operations.
- Models: application data.
- Styles: shared appearance.
- Helpers: small generic utilities.
- Resources: icons and images.
- Documentation: user and configuration guides.

## GitHub authentication

GitHub access is optional and supports two authentication methods:
- GitHub OAuth (`Sign in with GitHub`)
- A manually supplied Personal Access Token

Both methods are centralized through `GitHubAuthService`. Sensitive credentials must not be stored in plain text in `AppSettings` or catalog files.
