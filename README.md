# MBW.Tools.GhStandardContent

[![Generic Build](https://github.com/LordMike/MBW.Tools.GhStandardContent/actions/workflows/dotnet.yml/badge.svg)](https://github.com/LordMike/MBW.Tools.GhStandardContent/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/MBW.Tools.GhStandardContent.svg)](https://www.nuget.org/packages/MBW.Tools.GhStandardContent)

A .NET global tool for keeping shared files consistent across GitHub repositories. It plans changes before writing, updates repositories through pull requests, and can apply the same content safely to a local Git worktree.

## Install

```shell
dotnet tool install --global MBW.Tools.GhStandardContent
```

Update an existing installation with:

```shell
dotnet tool update --global MBW.Tools.GhStandardContent
```

The installed command is `gh-standard-content`.

## Quick start

Create a `repos.json` file and point each target path at a source file relative to that configuration:

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/LordMike/MBW.Tools.GhStandardContent/master/spec/RepositoryConfig.json",
  "content": {
    "standardContent": {
      ".gitignore": "standard_content/.gitignore",
      ".editorconfig": "standard_content/.editorconfig"
    },
    "standardDotnet": {
      "Directory.Build.props": "standard_content/Directory.Build.props"
    }
  },
  "repositories": {
    "example/api": {
      "standardContent": true,
      "standardDotnet": true
    },
    "example/docs": {
      "standardContent": true,
      "standardDotnet": false
    }
  }
}
```

Validate the configuration and all referenced source files:

```shell
gh-standard-content validate repos.json
```

Set a token without exposing it in the process list, then inspect all pending changes:

```shell
export GH_TOKEN="github-token"
gh-standard-content check repos.json
```

Apply the changes through dedicated branches and pull requests:

```shell
gh-standard-content apply repos.json
```

Merge generated pull requests once their content and CI are current:

```shell
gh-standard-content merge repos.json
```

PowerShell users can set the token with `$env:GH_TOKEN = "github-token"`. `GITHUB_TOKEN` is accepted as a fallback. GitHub Enterprise also supports `GH_ENTERPRISE_TOKEN` and `GITHUB_ENTERPRISE_TOKEN`.

## Commands

### `validate <CONFIG>`

Checks JSON/JSONC syntax, repository names, profile references, source-file availability, duplicate targets, reserved paths, and unsafe destination paths. It performs no repository or network access.

### `check <CONFIG>`

Reads the selected repositories and returns the exact add, update, and delete plan without writing. GitHub checks compare the default branch; an already-open update PR is reported separately as pending work. If its generated branch has the desired content but is behind the default branch, check mode reports the number of commits behind.

### `apply <CONFIG>`

Applies the calculated plan. GitHub mode creates or updates the configured branch and ensures an open pull request exists. When an existing generated branch is behind the default branch, it is rebuilt as one commit on the latest default commit and the existing PR is reused. No refresh is made when the default branch already contains the desired content. Local mode stages changed content before replacing files and writes metadata last.

### `merge <CONFIG>`

Checks each selected GitHub repository using the same content and pull-request assessment as `check` and `apply`.
A PR is squash-merged only when it contains exactly the calculated changes, has every configured label, is current
with the default branch, has a successful GitHub status-check rollup, and GitHub reports it as mergeable. Missing,
pending, failing, and blocked states are reported without changing the PR.

`merge --allow-updating` may create a missing PR or repair an outdated generated branch, unexpected PR changes,
missing labels, or a branch behind the default branch. A repaired PR is never merged in that invocation; rerun after
CI has completed. This makes a recurring automation loop converge safely through create/update, CI, and merge phases.

Useful operational options:

```text
-r, --repository <owner/name>  Select one or more configured repositories
--local <path>                 Use a local Git worktree instead of GitHub (check/apply only)
--branch <name>                Dedicated update branch
--orphaned-files <policy>      error, keep, or delete (default: error)
--parallelism <1-16>           Maximum concurrent GitHub repositories
--meta-reference <text>        Set metadata reference; empty removes it
--github-api <uri>             GitHub Enterprise API base URI
--proxy <uri>                  Explicit HTTP proxy
--allow-updating               Let merge create or repair PRs; never merges them in the same run
```

Run any command with `--help` for its complete option reference.

### Environment defaults

Operational options can be supplied through environment variables. Explicit CLI options override environment values,
which in turn override built-in defaults.

| Environment variable | CLI option |
| --- | --- |
| `GHSC_CONFIG` | `CONFIG` |
| `GHSC_REPOSITORIES` | `--repository` |
| `GHSC_LOCAL` | `--local` |
| `GHSC_GITHUB_API` | `--github-api` |
| `GHSC_PROXY` | `--proxy` |
| `GHSC_PARALLELISM` | `--parallelism` |
| `GHSC_BRANCH` | `--branch` |
| `GHSC_COMMIT_AUTHOR` | `--commit-author` |
| `GHSC_COMMIT_EMAIL` | `--commit-email` |
| `GHSC_LABELS` | `--label` |
| `GHSC_META_REFERENCE` | `--meta-reference` |
| `GHSC_ORPHANED_FILES` | `--orphaned-files` |

Use semicolon-separated values for repositories and labels, for example
`GHSC_REPOSITORIES="owner/one;owner/two"`. An explicit `CONFIG` argument overrides `GHSC_CONFIG`; commands and
presentation options remain CLI-only.

## Output

Interactive text output uses a single live progress bar followed by the final results table. Transient progress is
suppressed when output is redirected, in quiet mode, and for JSON output. Control text presentation with:

```text
--verbosity quiet|normal|detailed
--color auto|always|never
```

Automation can request a deterministic JSON document:

```shell
gh-standard-content check repos.json --format json
```

The JSON contract includes `schemaVersion`, command/result fields, aggregate counts, repository statuses, optional
status reasons/details, file operations, pull-request head and merge SHAs, and structured errors. Merge aggregates
include `merged`, `noChanges`, `notReady`, and `remediated`. File contents and credentials are never included.

Exit codes are stable:

| Code | Meaning |
| ---: | --- |
| 0 | The command completed its contract; merge merged everything or found no changes |
| 1 | Invalid invocation, configuration, or global preflight |
| 2 | Check found pending changes, or merge found/remediated repositories that are not yet merged |
| 3 | One or more repositories failed or need an orphan decision |
| 130 | Cancelled |

## Removed managed files

When `.standard_content.json` lists files that are no longer selected, the default `error` policy blocks that repository without affecting the rest of the batch. Choose explicitly:

```shell
gh-standard-content apply repos.json --orphaned-files keep
gh-standard-content apply repos.json --orphaned-files delete
```

`keep` stops managing the files but leaves their contents. `delete` removes them. If the final profile is disabled, the metadata file is removed last so the repository becomes unmanaged cleanly.

## Local worktrees and overrides

Use local mode to preview or apply the same plan to an existing Git worktree:

```shell
gh-standard-content check repos.json --local ../my-repo
gh-standard-content apply repos.json --local ../my-repo
```

The repository is resolved from `--repository`, its `origin` remote, or an unambiguous folder name. Destination paths are constrained to the worktree and cannot traverse symlinks, reparse points, `.git`, or parent directories.

Repositories can append local UTF-8 text to these standard files:

| Standard file | Local override |
| --- | --- |
| `.gitignore` | `_Local/.gitignore` |
| `.gitattributes` | `_Local/.gitattributes` |
| `.dockerignore` | `_Local/.dockerignore` |
| `.editorconfig` | `_Local/.editorconfig` |

## GitHub access

The token needs repository contents and pull-request access for private repositories, plus check/status read access for
`merge`. Write access is needed by `apply`, by `merge --allow-updating`, and to merge an eligible PR. Updating workflow
files may require additional workflow permission, depending on the token type. Requested labels must already exist in
the target repository.

The update branch is tool-owned and may be force-updated from the latest default branch. Existing open PRs are reused; missing PRs are repaired on a later run if branch creation previously succeeded but PR creation failed.

## Background

The project originated from the articles on [administering multiple GitHub repositories](https://blog.mbwarez.dk/gh-mass-administration/) and [managing standardized repository content](https://blog.mbwarez.dk/gh-mass-administration-content/).
