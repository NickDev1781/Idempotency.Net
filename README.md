# Idempotency.Net

**A lightweight, production‑ready idempotency library for ASP.NET Core with built‑in distributed locking and pluggable storage (Redis, PostgreSQL, In‑Memory).**

[![NuGet](https://img.shields.io/nuget/v/Idempotency.Net.svg)](https://www.nuget.org/packages/Idempotency.Net)
[![Build](https://github.com/NickDev1781/Idempotency.Net/actions/workflows/build.yml/badge.svg)](https://github.com/your-username/Idempotency.Net/actions)
[![Tests](https://img.shields.io/badge/tests-integration%20%E2%9C%93-green)](tests)

## Why Idempotency.Net?

Duplicate API calls are a constant threat in distributed systems. Whether it's a double payment, a duplicate order, or a retried webhook — idempotency is essential.

Existing solutions like [IdempotentAPI](https://github.com/ikyriak/IdempotentAPI)) are powerful, but they require **multiple NuGet packages, external locking libraries, and verbose configuration**.

**Idempotency.Net** gives you the same guarantees with **one package, one line of configuration, and zero external dependencies** for distributed locking.

## Features

- 🛡️ **Guaranteed exactly‑once execution** – distributed locking prevents race conditions even under concurrent requests.
- 🔌 **Pluggable storage** – Redis, PostgreSQL, or In‑Memory (for development/testing).
- 🧘 **Minimal setup** – install one package, add one line to `Program.cs`, apply an attribute.
- ⚙️ **Flexible configuration** – customizable key header, TTL, lock timeouts.
- 🧪 **Integration‑tested** – tested against real Redis and PostgreSQL containers via Testcontainers.
- 🏗️ **Production‑ready** – built on `NpgsqlDataSource`, `StackExchange.Redis`, and modern .NET practices.

## Quick Start

### 1. Install the package

Choose your storage:

```bash
dotnet add package Idempotency.Net.Redis
# or
dotnet add package Idempotency.Net.PostgreSql
