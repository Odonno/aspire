// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using Xunit;

namespace Aspire.Hosting.SurrealDb.Tests;

public class AddSurrealServerTests
{
    [Fact]
    public void AddSurrealServerAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var surrealServer = appBuilder.AddSurrealServer("surreal");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", surrealServer.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddSurrealServerDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var surrealServer = appBuilder.AddSurrealServer("surreal");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", surrealServer.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddSurrealServerContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddSurrealServer("surreal");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SurrealDbServerResource>());
        Assert.Equal("surreal", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8000, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SurrealDbContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SurrealDbContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SurrealDbContainerImageTags.Registry, containerAnnotation.Registry);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("SURREAL_USER", env.Key);
                Assert.NotNull(env.Value);
            },
            env =>
            {
                Assert.Equal("SURREAL_PASS", env.Key);
                Assert.NotNull(env.Value);
                Assert.True(env.Value.Length >= 8);
            });
    }

    [Fact]
    public async Task SurrealServerCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration["Parameters:pass"] = "p@ssw0rd1";

        var pass = appBuilder.AddParameter("pass");
        appBuilder
            .AddSurrealServer("surreal", null, pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8000));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<SurrealDbServerResource>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);

        Assert.Equal("Server=ws://127.0.0.1:8000/rpc;User=root;Password=p@ssw0rd1", connectionString);
        Assert.Equal("Server=ws://{surreal.bindings.tcp.host}:{surreal.bindings.tcp.port}/rpc;User=root;Password={pass.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task SurrealServerDatabaseCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration["Parameters:pass"] = "p@ssw0rd1";

        var pass = appBuilder.AddParameter("pass");
        appBuilder
            .AddSurrealServer("surreal", null, pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8000))
            .AddDatabase("db", "myns", "mydb");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var surrealResource = Assert.Single(appModel.Resources.OfType<SurrealDbDatabaseResource>());
        var connectionStringResource = (IResourceWithConnectionString)surrealResource;
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

        Assert.Equal("Server=ws://127.0.0.1:8000/rpc;User=root;Password=p@ssw0rd1;Namespace=myns;Database=mydb", connectionString);
        Assert.Equal("{surreal.connectionString};Namespace=myns;Database=mydb", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddSurrealServer("surreal");
        var db = sqlServer.AddDatabase("db");

        var serverManifest = await ManifestUtils.GetManifest(sqlServer.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server=ws://{surreal.bindings.tcp.host}:{surreal.bindings.tcp.port}/rpc;User=root;Password={surreal-password.value}",
              "image": "{{SurrealDbContainerImageTags.Registry}}/{{SurrealDbContainerImageTags.Image}}:{{SurrealDbContainerImageTags.Tag}}",
              "entrypoint": "/surreal",
              "args": [
                "start",
                "--auth"
              ],
              "env": {
                "SURREAL_USER": "root",
                "SURREAL_PASS": "{surreal-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{surreal.connectionString};Namespace=test;Database=test"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithPasswordParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var pass = builder.AddParameter("pass");

        var surrealServer = builder.AddSurrealServer("surreal", null, pass);
        var serverManifest = await ManifestUtils.GetManifest(surrealServer.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server=ws://{surreal.bindings.tcp.host}:{surreal.bindings.tcp.port}/rpc;User=root;Password={pass.value}",
              "image": "{{SurrealDbContainerImageTags.Registry}}/{{SurrealDbContainerImageTags.Image}}:{{SurrealDbContainerImageTags.Tag}}",
              "entrypoint": "/surreal",
              "args": [
                "start",
                "--auth"
              ],
              "env": {
                "SURREAL_USER": "root",
                "SURREAL_PASS": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db = builder.AddSurrealServer("surreal1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddSurrealServer("surreal1")
            .AddDatabase("db");

        var db = builder.AddSurrealServer("surreal2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var surrealServer1 = builder.AddSurrealServer("surreal1");

        var db1 = surrealServer1.AddDatabase("db1", "ns", "customers1");
        var db2 = surrealServer1.AddDatabase("db2", "ns", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("{surreal1.connectionString};Namespace=ns;Database=customers1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{surreal1.connectionString};Namespace=ns;Database=customers2", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db1 = builder.AddSurrealServer("surreal1")
            .AddDatabase("db1", "ns", "imports");

        var db2 = builder.AddSurrealServer("surreal2")
            .AddDatabase("db2", "ns", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("{surreal1.connectionString};Namespace=ns;Database=imports", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{surreal2.connectionString};Namespace=ns;Database=imports", db2.Resource.ConnectionStringExpression.ValueExpression);
    }
}
