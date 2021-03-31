# MBW.Tools.GhStandardContent [![Generic Build](https://github.com/LordMike/MBW.Tools.GhStandardContent/actions/workflows/dotnet.yml/badge.svg)](https://github.com/LordMike/MBW.Tools.GhStandardContent/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/MBW.Tools.GhStandardContent.svg)](https://www.nuget.org/packages/MBW.Tools.GhStandardContent) [![GHPackages](https://img.shields.io/badge/package-alpha-green)](https://github.com/LordMike/MBW.Tools.GhStandardContent/packages/703366)

Tool to streamline content in a Github repository from a spec.

This is part of [a blog post on how to administer multiple Github repositories](https://blog.mbwarez.dk/gh-mass-administration/).

## Installation

Run `dotnet tool install -g MBW.Tools.GhStandardContent`. After this, `gh-standard-content` should be in your PATH.

## Usage

Read more on the [blog post about managing standardized content on Github repositories](https://blog.mbwarez.dk/gh-mass-administration-content/). In short, you need a Github token and a `repos.json` file which describes your content.

Create a `repos.json` with the following:

```jsonc
{
  "content": {
    // Define as many sets of files as you want
    "standardContent": {
      ".gitignore": "standard_content\\.gitignore"
    },
    // Each set can describe multiple files, and where they're located locally
    "standardDotnetNuget": {
      // To modify workflow files, you need a special scope: workflow
      ".github/workflows/dotnet.yml": "standard_content\\dotnet.yml",
      ".github/workflows/nuget.yml": "standard_content\\nuget.yml",
      "Directory.Build.props": "standard_content\\Directory.Build.props"
    }
  },
  "repositories": {
    // Describe each repository, with a property indicating that a set applies.
    "LordMike/MBW.Client.BlueRiiotApi": {
      "standardContent": true,
      "standardDotnetNuget": true
    },
    ...
  }
}

```

Run the tool with `gh-standard-content -t My_Token repos.json` to create pull requests on all repositories that are out of date.

## Other options

Run `gh-standard-content --help` for more parameters.
