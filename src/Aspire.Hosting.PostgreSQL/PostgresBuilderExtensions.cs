// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Postgres;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding PostgreSQL resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class PostgresBuilderExtensions
{
    private const string UserEnvVarName = "POSTGRES_USER";
    private const string PasswordEnvVarName = "POSTGRES_PASSWORD";

    /// <summary>
    /// Adds a PostgreSQL resource to the application model. A container is used for local development. This version the package defaults to the 16.2 tag of the postgres container image
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the PostgreSQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port used when launching the container. If null a random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<PostgresServerResource> AddPostgres(this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var postgresServer = new PostgresServerResource(name, userName?.Resource, passwordParameter);
        return builder.AddResource(postgresServer)
                      .WithEndpoint(port: port, targetPort: 5432, name: PostgresServerResource.PrimaryEndpointName) // Internal port is always 5432.
                      .WithImage(PostgresContainerImageTags.Image, PostgresContainerImageTags.Tag)
                      .WithImageRegistry(PostgresContainerImageTags.Registry)
                      .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "scram-sha-256")
                      .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256 --auth-local=scram-sha-256")
                      .WithEnvironment(context =>
                      {
                          context.EnvironmentVariables[UserEnvVarName] = postgresServer.UserNameReference;
                          context.EnvironmentVariables[PasswordEnvVarName] = postgresServer.PasswordParameter;
                      });
    }

    /// <summary>
    /// Adds a PostgreSQL database to the application model.
    /// </summary>
    /// <param name="builder">The PostgreSQL server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<PostgresDatabaseResource> AddDatabase(this IResourceBuilder<PostgresServerResource> builder, string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        builder.Resource.AddDatabase(name, databaseName);
        var postgresDatabase = new PostgresDatabaseResource(name, databaseName, builder.Resource);
        return builder.ApplicationBuilder.AddResource(postgresDatabase);
    }

    /// <summary>
    /// Adds a pgAdmin 4 administration and development platform for PostgreSQL to the application model. This version the package defaults to the 8.3 tag of the dpage/pgadmin4 container image
    /// </summary>
    /// <param name="builder">The PostgreSQL server resource builder.</param>
    /// <param name="configureContainer">Callback to configure PgAdmin container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithPgAdmin<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<PgAdminContainerResource>>? configureContainer = null, string? containerName = null) where T : PostgresServerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<PgAdminContainerResource>().SingleOrDefault() is { } existingPgAdminResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingPgAdminResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= $"{builder.Resource.Name}-pgadmin";

            var pgAdminContainer = new PgAdminContainerResource(containerName);
            var pgAdminContainerBuilder = builder.ApplicationBuilder.AddResource(pgAdminContainer)
                                                 .WithImage(PostgresContainerImageTags.PgAdminImage, PostgresContainerImageTags.PgAdminTag)
                                                 .WithImageRegistry(PostgresContainerImageTags.PgAdminRegistry)
                                                 .WithHttpEndpoint(targetPort: 80, name: "http")
                                                 .WithEnvironment(SetPgAdminEnvironmentVariables)
                                                 .WithBindMount(Path.GetTempFileName(), "/pgadmin4/servers.json")
                                                 .ExcludeFromManifest();

            builder.ApplicationBuilder.Eventing.Subscribe<AfterEndpointsAllocatedEvent>((e, ct) =>
            {
                var serverFileMount = pgAdminContainer.Annotations.OfType<ContainerMountAnnotation>().Single(v => v.Target == "/pgadmin4/servers.json");
                var postgresInstances = builder.ApplicationBuilder.Resources.OfType<PostgresServerResource>();

                var serverFileBuilder = new StringBuilder();

                using var stream = new FileStream(serverFileMount.Source!, FileMode.Create);
                using var writer = new Utf8JsonWriter(stream);
                // Need to grant read access to the config file on unix like systems.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(serverFileMount.Source!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }

                var serverIndex = 1;

                writer.WriteStartObject();
                writer.WriteStartObject("Servers");

                foreach (var postgresInstance in postgresInstances)
                {
                    if (postgresInstance.PrimaryEndpoint.IsAllocated)
                    {
                        var endpoint = postgresInstance.PrimaryEndpoint;

                        writer.WriteStartObject($"{serverIndex}");
                        writer.WriteString("Name", postgresInstance.Name);
                        writer.WriteString("Group", "Servers");
                        writer.WriteString("Host", endpoint.ContainerHost);
                        writer.WriteNumber("Port", endpoint.Port);
                        writer.WriteString("Username", "postgres");
                        writer.WriteString("SSLMode", "prefer");
                        writer.WriteString("MaintenanceDB", "postgres");
                        writer.WriteString("PasswordExecCommand", $"echo '{postgresInstance.PasswordParameter.Value}'"); // HACK: Generating a pass file and playing around with chmod is too painful.
                        writer.WriteEndObject();
                    }

                    serverIndex++;
                }

                writer.WriteEndObject();
                writer.WriteEndObject();

                return Task.CompletedTask;
            });

            configureContainer?.Invoke(pgAdminContainerBuilder);

            return builder;
        }
    }

    /// <summary>
    /// Configures the host port that the PGAdmin resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for PGAdmin.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for PGAdmin.</returns>
    public static IResourceBuilder<PgAdminContainerResource> WithHostPort(this IResourceBuilder<PgAdminContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    private static void SetPgAdminEnvironmentVariables(EnvironmentCallbackContext context)
    {
        // Disables pgAdmin authentication.
        context.EnvironmentVariables.Add("PGADMIN_CONFIG_MASTER_PASSWORD_REQUIRED", "False");
        context.EnvironmentVariables.Add("PGADMIN_CONFIG_SERVER_MODE", "False");

        // You need to define the PGADMIN_DEFAULT_EMAIL and PGADMIN_DEFAULT_PASSWORD or PGADMIN_DEFAULT_PASSWORD_FILE environment variables.
        context.EnvironmentVariables.Add("PGADMIN_DEFAULT_EMAIL", "admin@domain.com");
        context.EnvironmentVariables.Add("PGADMIN_DEFAULT_PASSWORD", "admin");
    }

    /// <summary>
    /// Adds a named volume for the data folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<PostgresServerResource> WithDataVolume(this IResourceBuilder<PostgresServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.CreateVolumeName(builder, "data"),
            "/var/lib/postgresql/data", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<PostgresServerResource> WithDataBindMount(this IResourceBuilder<PostgresServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        return builder.WithBindMount(source, "/var/lib/postgresql/data", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the init folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<PostgresServerResource> WithInitBindMount(this IResourceBuilder<PostgresServerResource> builder, string source, bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        return builder.WithBindMount(source, "/docker-entrypoint-initdb.d", isReadOnly);
    }
}
