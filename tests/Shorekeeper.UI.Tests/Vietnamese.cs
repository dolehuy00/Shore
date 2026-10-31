using System.Runtime.CompilerServices;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.UI.Tests;

/// <summary>The words the tests expect are the Vietnamese ones (the keys), whatever the machine's language.</summary>
internal static class Vietnamese
{
    [ModuleInitializer]
    internal static void Use() => L.IsVietnamese = true;
}
