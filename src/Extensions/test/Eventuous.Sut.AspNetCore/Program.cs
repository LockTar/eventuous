using System.Text.Json;
using Eventuous.Sut.AspNetCore;
using Eventuous.Sut.Domain;
using Eventuous.Testing;
using Microsoft.AspNetCore.Http.Json;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using BookingService = Eventuous.Sut.AspNetCore.BookingService;

DefaultEventSerializer.SetDefaultSerializer(
    new DefaultEventSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb))
);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCommandService<BookingService, BookingState>();
builder.Services.AddAggregateStore<InMemoryEventStore>();
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb));

var app = builder.Build();

var config = app.Services.GetService<ConfigureWebApplication>();
config?.Invoke(app);

app.Run();

public partial class Program;
