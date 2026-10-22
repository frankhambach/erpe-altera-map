// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

public interface IProjectionService
{
    // Longitude and latitude in degrees to the projection's own plane, the space Rescale maps
    // into. The inverse of Unproject.
    Coordinate Project(Coordinate coordinate);

    Coordinate Rescale(Coordinate coordinate, Envelope sourceEnvelope);

    // The projection's own plane back to the source envelope. The inverse of Rescale.
    Coordinate Unscale(Coordinate coordinate, Envelope sourceEnvelope);

    // A longitude past 180 either way is returned as it is rather than clamped, because what
    // lies beyond the rim is land drawn past it, and IRimService decides where that goes.
    Coordinate Unproject(Coordinate coordinate);
}