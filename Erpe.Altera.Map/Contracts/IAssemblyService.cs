// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

public interface IAssemblyService
{
    // Returns the faces that were left out, which are the ones whose traced ring cannot be
    // made into valid geometry.
    IReadOnlyList<Face> Assemble(ArcGraph arcGraph, IEnumerable<Country> countries);
}
