// <copyright file="IProjectionService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

public interface IProjectionService
{
    LineString Unproject(CoordinateSequence coordinateSequence, Envelope sourceEnvelope);
}