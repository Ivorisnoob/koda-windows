using System.Runtime.CompilerServices;

// The tests reach the engine's internals, the way the sibling app's tests reach
// its @testable import: a parser's helper is part of what is being tested.
[assembly: InternalsVisibleTo("Koda.Core.Tests")]
