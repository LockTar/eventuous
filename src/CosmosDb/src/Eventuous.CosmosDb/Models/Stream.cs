// Copyright (C) Ubiquitous AS. All rights reserved
// Licensed under the Apache License, Version 2.0.

namespace Eventuous.CosmosDb.Models;
public record StreamData(int StreamId, string StreamName, int Version);

public record MessageData(Guid MessageId, string MessageType, int StreamId,
	int StreamPosition, int GlobalPosition, string JsonData, string JsonMetadata, DateTime Created);
