# Dalmarkit

Dalmarkit is an opinionated .NET utility library that simplifies the building of AWS Cognito secured JSON REST APIs with audit trails using ASP.NET Core and Entity Framework Core with PostgreSQL.

## Getting started

Install the [Dalmarkit Common package][common] with [NuGet][nuget]:

```dotnetcli
dotnet add package Dalmarkit.Common
```

Install the [Dalmarkit Entity Framework Core package][efcore] with [NuGet][nuget]:

```dotnetcli
dotnet add package Dalmarkit.EntityFrameworkCore
```

Install the [Dalmarkit ASP.NET Core package][aspnetcore] with [NuGet][nuget]:

```dotnetcli
dotnet add package Dalmarkit.AspNetCore
```

Install the [Dalmarkit Blockchain package][blockchain] with [NuGet][nuget] (optional):

```dotnetcli
dotnet add package Dalmarkit.Blockchain
```

Install the [Dalmarkit Cloud package][cloud] with [NuGet][nuget] (optional):

```dotnetcli
dotnet add package Dalmarkit.Cloud
```

## Usage

Please see [Dalmarkit-sample-webapi][sample] for sample implementation of a ASP.NET Core web API using [Dalmarkit][repository].

## Feedback

Dalmarkit is released as open source under the [Apache License 2.0 license][license]. Bug reports and contributions are welcome at our [GitHub repository][repository].

<!-- LINKS -->

[aspnetcore]: https://www.nuget.org/packages/Dalmarkit.AspNetCore
[blockchain]: https://www.nuget.org/packages/Dalmarkit.Blockchain
[cloud]: https://www.nuget.org/packages/Dalmarkit.Cloud
[common]: https://www.nuget.org/packages/Dalmarkit.Common
[efcore]: https://www.nuget.org/packages/Dalmarkit.EntityFrameworkCore
[license]: https://github.com/tigerwong-hk/Dalmarkit/blob/main/LICENSE
[nuget]: https://www.nuget.org
[repository]: https://github.com/tigerwong-hk/Dalmarkit
[sample]: https://github.com/tigerwong-hk/Dalmarkit-sample-webapi
