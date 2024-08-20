// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddAzureCosmosDB("cosmos")
                .AddDatabase("db")
                .RunAsEmulator();

var cosmosConnString = () => $"AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint=https://127.0.0.1:{db.GetEndpoint("emulator").Port};DisableServerCertificateValidation=True;";

builder.AddProject<Projects.CosmosEndToEnd_ApiService>("api")
        .WithEnvironment("CosmosConnString", cosmosConnString)
       .WithExternalHttpEndpoints()
       .WithReference(db);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
