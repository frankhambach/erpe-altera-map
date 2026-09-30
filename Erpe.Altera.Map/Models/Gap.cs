// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Immutable;

using NetTopologySuite.Geometries;

public sealed class Gap(LinearRing boundary, ImmutableArray<Coast> coasts)
{
    public LinearRing Boundary { get; } = boundary;

    public ImmutableArray<Coast> Coasts { get; } = coasts;
}