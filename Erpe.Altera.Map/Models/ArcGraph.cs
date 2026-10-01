// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

public sealed class ArcGraph
{
    public IList<Arc> Arcs { get; } = new List<Arc>();

    public IList<Face> Faces { get; } = new List<Face>();

    public IList<Node> Nodes { get; } = new List<Node>();
}
