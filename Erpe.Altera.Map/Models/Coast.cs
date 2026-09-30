// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public sealed class Coast(LineString line, Shape shape)
{
    public LineString Line { get; } = line;

    public Shape Shape { get; } = shape;
}