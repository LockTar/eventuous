using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Eventuous.CosmosDb.Models;
using Eventuous.EventStore;
using Eventuous.Tools;
using Microsoft.Azure.Cosmos;
using static Eventuous.Diagnostics.PersistenceEventSource;
using Container = Microsoft.Azure.Cosmos.Container;

namespace Eventuous.CosmosDb;

public class CosmosdbEventStore : IEventStore {
	readonly ILogger<CosmosdbEventStore>? _logger;
	readonly CosmosClient _client;
	readonly IEventSerializer _serializer;
	readonly IMetadataSerializer _metaSerializer;

	public CosmosdbEventStore(
		CosmosClient client,
		IEventSerializer? serializer = null,
		IMetadataSerializer? metaSerializer = null,
		ILogger<CosmosdbEventStore>? logger = null
	) {
		_logger = logger;
		_client = Ensure.NotNull(client);
		_serializer = serializer ?? DefaultEventSerializer.Instance;
		_metaSerializer = metaSerializer ?? DefaultMetadataSerializer.Instance;
	}

	public async Task<bool> StreamExists(StreamName stream, CancellationToken cancellationToken) {
		Container container = GetContainer();

		// Create query using a SQL string and parameters
		var query = new QueryDefinition(
			query: "SELECT * FROM streams s WHERE s.StreamName = @name"
		).WithParameter("@name", stream.ToString());

		using FeedIterator<StreamEvent> feed = container.GetItemQueryIterator<StreamEvent>(
			queryDefinition: query
		);
		
		while (feed.HasMoreResults)
		{
			FeedResponse<StreamEvent> response = await feed.ReadNextAsync();

			foreach (StreamEvent item in response)
			{
				Console.WriteLine($"Found item:\t{item.Id}\t{item.Position}");
			}

			return response.Count > 0 ? true : false;
		}

		return false;

		//var read = _client.ReadStreamAsync(
		//	Direction.Backwards,
		//	stream,
		//	StreamPosition.End,
		//	1,
		//	cancellationToken: cancellationToken
		//);

		//using var readState = read.ReadState;
		//var state = await readState.NoContext();
		//return state == ReadState.Ok;
	}

	public async Task<AppendEventsResult> AppendEvents(
		StreamName stream,
		ExpectedStreamVersion expectedVersion,
		IReadOnlyCollection<StreamEvent> events,
		CancellationToken cancellationToken
	) {
		Container container = GetContainer();

		var proposedEvents = events.Select(ToEventData);

		DateTime created = DateTime.UtcNow;

		// Create query using a SQL string and parameters
		var query = new QueryDefinition(
			query: "SELECT * FROM streams s WHERE s.StreamName = @name"
		).WithParameter("@name", stream.ToString());

		using FeedIterator<StreamData> feed = container.GetItemQueryIterator<StreamData>(
			queryDefinition: query
		);

		int? expectedVersion = null;
		int? streamId = null;

		while (feed.HasMoreResults)
		{
			FeedResponse<StreamData> response = await feed.ReadNextAsync();

			var streamResponse = response.FirstOrDefault();

		}





		container.CreateTransactionalBatch()

		EventData createdItem = await container.CreateItemAsync<EventData>(
			item: newItem,
			partitionKey: new PartitionKey("")
		);




		var resultTask = expectedVersion == ExpectedStreamVersion.NoStream
			? _client.AppendToStreamAsync(
				stream,
				StreamState.NoStream,
				proposedEvents,
				cancellationToken: cancellationToken
			) : AnyOrNot(
				expectedVersion,
				() => _client.AppendToStreamAsync(
					stream,
					StreamState.Any,
					proposedEvents,
					cancellationToken: cancellationToken
				),
				() => _client.AppendToStreamAsync(
					stream,
					expectedVersion.AsStreamRevision(),
					proposedEvents,
					cancellationToken: cancellationToken
				)
			);

		return TryExecute(
			async () => {
				var result = await resultTask.NoContext();

				return new AppendEventsResult(
					result.LogPosition.CommitPosition,
					result.NextExpectedStreamRevision.ToInt64()
				);
			},
			stream,
			() => new ErrorInfo("Unable to appends events to {Stream}", stream),
			(s, ex) => {
				Log.UnableToAppendEvents(stream, ex);
				return new AppendToStreamException(s, ex);
			}
		);

		EventData ToEventData(StreamEvent streamEvent) {
			var (eventType, contentType, payload) = _serializer.SerializeEvent(streamEvent.Payload!);

			return new EventData(
				Uuid.FromGuid(streamEvent.Id),
				eventType,
				payload,
				_metaSerializer.Serialize(streamEvent.Metadata),
				contentType
			);
		}
	}

	private Container GetContainer() {
		// Database reference with creation if it does not already exist
		Database database = _client.GetDatabase(id: "cosmicworks");

		// Container reference with creation if it does not alredy exist
		Container container = database.GetContainer(id: "products");
		return container;
	}

	public Task<StreamEvent[]> ReadEvents(
		StreamName stream,
		StreamReadPosition start,
		int count,
		CancellationToken cancellationToken
	) {
		var read = _client.ReadStreamAsync(
			Direction.Forwards,
			stream,
			start.AsStreamPosition(),
			count,
			cancellationToken: cancellationToken
		);

		return TryExecute(
			async () => {
				var resolvedEvents = await read.ToArrayAsync(cancellationToken).NoContext();
				return ToStreamEvents(resolvedEvents);
			},
			stream,
			() => new ErrorInfo(
				"Unable to read {Count} starting at {Start} events from {Stream}",
				count,
				start,
				stream
			),
			(s, ex) => new ReadFromStreamException(s, ex)
		);
	}

	public Task<StreamEvent[]> ReadEventsBackwards(
		StreamName stream,
		int count,
		CancellationToken cancellationToken
	) {
		var read = _client.ReadStreamAsync(
			Direction.Backwards,
			stream,
			StreamPosition.End,
			count,
			resolveLinkTos: true,
			cancellationToken: cancellationToken
		);

		return TryExecute(
			async () => {
				var resolvedEvents = await read.ToArrayAsync(cancellationToken).NoContext();
				return ToStreamEvents(resolvedEvents);
			},
			stream,
			() => new ErrorInfo(
				"Unable to read {Count} events backwards from {Stream}",
				count,
				stream
			),
			(s, ex) => new ReadFromStreamException(s, ex)
		);
	}

	public Task TruncateStream(
		StreamName stream,
		StreamTruncatePosition truncatePosition,
		ExpectedStreamVersion expectedVersion,
		CancellationToken cancellationToken
	) {
		var meta = new StreamMetadata(truncateBefore: truncatePosition.AsStreamPosition());

		return TryExecute(
			() => AnyOrNot(
				expectedVersion,
				() => _client.SetStreamMetadataAsync(
					stream,
					StreamState.Any,
					meta,
					cancellationToken: cancellationToken
				),
				() => _client.SetStreamMetadataAsync(
					stream,
					expectedVersion.AsStreamRevision(),
					meta,
					cancellationToken: cancellationToken
				)
			),
			stream,
			() => new ErrorInfo(
				"Unable to truncate stream {Stream} at {Position}",
				stream,
				truncatePosition
			),
			(s, ex) => new TruncateStreamException(s, ex)
		);
	}

	public Task DeleteStream(
		StreamName stream,
		ExpectedStreamVersion expectedVersion,
		CancellationToken cancellationToken
	)
		=> TryExecute(
			() => AnyOrNot(
				expectedVersion,
				() => _client.DeleteAsync(
					stream,
					StreamState.Any,
					cancellationToken: cancellationToken
				),
				() => _client.DeleteAsync(
					stream,
					expectedVersion.AsStreamRevision(),
					cancellationToken: cancellationToken
				)
			),
			stream,
			() => new ErrorInfo("Unable to delete stream {Stream}", stream),
			(s, ex) => new DeleteStreamException(s, ex)
		);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	async Task<T> TryExecute<T>(
		Func<Task<T>> func,
		string stream,
		Func<ErrorInfo> getError,
		Func<string, Exception, Exception> getException
	) {
		try
		{
			return await func().NoContext();
		}
		catch (StreamNotFoundException)
		{
			_logger?.LogWarning("Stream {Stream} not found", stream);
			throw new StreamNotFound(stream);
		}
		catch (Exception ex)
		{
			var (message, args) = getError();
			// ReSharper disable once TemplateIsNotCompileTimeConstantProblem
			_logger?.LogWarning(ex, message, args);
			throw getException(stream, ex);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Task<T> AnyOrNot<T>(
		ExpectedStreamVersion version,
		Func<Task<T>> whenAny,
		Func<Task<T>> otherwise
	)
		=> version == ExpectedStreamVersion.Any ? whenAny() : otherwise();

	StreamEvent ToStreamEvent(ResolvedEvent resolvedEvent) {
		var deserialized = _serializer.DeserializeEvent(
			resolvedEvent.Event.Data.Span,
			resolvedEvent.Event.EventType,
			resolvedEvent.Event.ContentType
		);

		return deserialized switch {
			SuccessfullyDeserialized success => AsStreamEvent(success.Payload),
			FailedToDeserialize failed => throw new SerializationException(
				$"Can't deserialize {resolvedEvent.Event.EventType}: {failed.Error}"
			),
			_ => throw new SerializationException("Unknown deserialization result")
		};

		Metadata? DeserializeMetadata() {
			var meta = resolvedEvent.Event.Metadata.Span;

			try
			{
				return meta.Length == 0 ? null : _metaSerializer.Deserialize(meta);
			}
			catch (MetadataDeserializationException e)
			{
				_logger?.LogWarning(
					e,
					"Failed to deserialize metadata at {Stream}:{Position}",
					resolvedEvent.Event.EventStreamId,
					resolvedEvent.Event.EventNumber
				);

				return null;
			}
		}

		StreamEvent AsStreamEvent(object payload)
			=> new(
				resolvedEvent.Event.EventId.ToGuid(),
				payload,
				DeserializeMetadata() ?? new Metadata(),
				resolvedEvent.Event.ContentType,
				resolvedEvent.OriginalEventNumber.ToInt64()
			);
	}

	StreamEvent[] ToStreamEvents(ResolvedEvent[] resolvedEvents)
		=> resolvedEvents
			.Where(x => !x.Event.EventType.StartsWith("$"))
			.Select(e => ToStreamEvent(e))
			.ToArray();

	record ErrorInfo(string Message, params object[] Args);
}
