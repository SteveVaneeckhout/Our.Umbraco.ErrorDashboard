# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [18.0.0] - 2026-09-26

Versioning now follows the Umbraco major: 18.x targets Umbraco 18, and a 17.x line on the
`v17/main` branch targets the Umbraco 17 LTS. No functional change from 1.0.0.

## [1.0.0] - 2026-09-19

First release. Targets Umbraco 18 deliberately, ahead of the v21 LTS, for teams happy to be early.

### Added

- Network Error Logging collector, with a public endpoint that is rate limited, size capped and host validated.
- Overview, Error Pages and Error Alerts dashboards in the Settings section.
- Anomaly alerting on median + median absolute deviation, with per-host cooldown and self-service email subscriptions.
- Works on SQL Server and SQLite; no provider-specific SQL.
- English and Dutch translations, including the alert email.
