// Copyright (C) Ubiquitous AS. All rights reserved
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NodaTime.Serialization.SystemTextJson;
using RestSharp.Serializers.Json;

namespace Eventuous.Tests.AspNetCore.Web.Fixture;

using static SutBookingCommands;

public class ServerFixture {
    readonly AutoFixture.Fixture _fixture = new();

    public ServerFixture(
            WebApplicationFactory<Program> factory,
            ITestOutputHelper              output,
            Action<IServiceCollection>?    register  = null,
            ConfigureWebApplication?       configure = null
        ) {
        var builder = factory
            .WithWebHostBuilder(
                builder => {
                    builder
                        .ConfigureServices(
                            services => {
                                register?.Invoke(services);
                                if (configure != null) services.AddSingleton(configure);
                            }
                        )
                        .ConfigureLogging(x => x.AddXunit(output).AddConsole().SetMinimumLevel(LogLevel.Debug));
                }
            );
        builder.Server.PreserveExecutionContext = false;

        _app = builder;
    }

    readonly JsonSerializerOptions          _options = new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    readonly WebApplicationFactory<Program> _app;

    public RestClient GetClient() {
        return new RestClient(
            _app.CreateClient(),
            disposeHttpClient: true,
            configureSerialization: s => s.UseSerializer(() => new SystemTextJsonSerializer(_options))
        );
    }

    public T Resolve<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public Task<StreamEvent[]> ReadStream<T>(string id)
        => Resolve<IEventStore>().ReadEvents(StreamName.For<T>(id), StreamReadPosition.Start, 100, default);

    internal BookRoom GetBookRoom() {
        var now  = new DateTime(2023, 10, 1);
        var date = LocalDate.FromDateTime(now);

        return new(_fixture.Create<string>(), _fixture.Create<string>(), date, date.PlusDays(1), 100, "guest");
    }

    internal NestedCommands.NestedBookRoom GetNestedBookRoom(DateTime? dateTime = null) {
        var date = LocalDate.FromDateTime(dateTime ?? DateTime.Now);

        return new(_fixture.Create<string>(), _fixture.Create<string>(), date, date.PlusDays(1), 100, "guest");
    }

    public async Task<string> ExecuteRequest<TCommand, TResult>(TCommand cmd, string route, string id)
        where TCommand : class where TResult : State<TResult>, new() {
        using var client = GetClient();

        var request  = new RestRequest(route).AddJsonBody(cmd);
        var response = await client.ExecutePostAsync<OkResult<TResult>>(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return response.Content!;
    }
}
