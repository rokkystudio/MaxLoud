using System;
using System.Collections;
using System.IO;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 ||
            !(string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Usage: EndpointToggleProbe on|off");
            return 2;
        }

        var enabled = string.Equals(
            args[0],
            "on",
            StringComparison.OrdinalIgnoreCase);

        var maxLoudAssemblyPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "bin",
                "Release",
                "MaxLoud.dll"));

        var assembly = Assembly.LoadFrom(maxLoudAssemblyPath);
        var managerType = assembly.GetType(
            "MaxLoud.AudioEndpointManager",
            true);

        var manager = Activator.CreateInstance(
            managerType,
            true);

        try
        {
            var endpoints = (IEnumerable)managerType
                .GetMethod("GetActiveRenderEndpoints")
                .Invoke(manager, null);

            object selectedEndpoint = null;

            foreach (var endpoint in endpoints)
            {
                var endpointType = endpoint.GetType();
                var isDefault = (bool)endpointType
                    .GetProperty("IsDefault")
                    .GetValue(endpoint);

                if (selectedEndpoint == null || isDefault)
                {
                    selectedEndpoint = endpoint;
                }

                if (isDefault)
                {
                    break;
                }
            }

            if (selectedEndpoint == null)
            {
                Console.Error.WriteLine("No active render endpoint found.");
                return 3;
            }

            var selectedType = selectedEndpoint.GetType();
            var endpointName = (string)selectedType
                .GetProperty("Name")
                .GetValue(selectedEndpoint);

            var getter = managerType.GetMethod(
                "GetSystemEnhancementsEnabled");
            var setter = managerType.GetMethod(
                "SetSystemEnhancementsEnabled");

            var before = (bool)getter.Invoke(
                manager,
                new[] { selectedEndpoint });

            setter.Invoke(
                manager,
                new object[] { selectedEndpoint, enabled });

            var after = (bool)getter.Invoke(
                manager,
                new[] { selectedEndpoint });

            Console.WriteLine("Endpoint: " + endpointName);
            Console.WriteLine("Before: " + before);
            Console.WriteLine("Requested: " + enabled);
            Console.WriteLine("After: " + after);
            return after == enabled ? 0 : 4;
        }
        finally
        {
            if (manager is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
