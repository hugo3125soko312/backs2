using System;
using System.Linq;
using System.Reflection;
using ScottPlot;

class PlotApiCheck {
    public static void Check() {
        Type plotType = typeof(ScottPlot.Plot);
        Console.WriteLine("Plot type: " + plotType.Namespace + "." + plotType.Name);
        
        var methods = plotType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Console.WriteLine("\nPublic methods:");
        foreach (var m in methods.OrderBy(x => x.Name))
            Console.WriteLine("  " + m.Name);
        
        var axesProp = plotType.GetProperty("Axes");
        if (axesProp != null) {
            Console.WriteLine("\nAxes property type: " + axesProp.PropertyType.Namespace + "." + axesProp.PropertyType.Name);
            var axesType = axesProp.PropertyType;
            var axesMethods = axesType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            Console.WriteLine("Axes methods:");
            foreach (var m in axesMethods.OrderBy(x => x.Name))
                Console.WriteLine("  " + m.Name);
        }
        
        var xAxisProp = axesProp?.PropertyType.GetProperty("XAxis");
        if (xAxisProp != null) {
            Console.WriteLine("\nXAxis property type: " + xAxisProp.PropertyType.Namespace + "." + xAxisProp.PropertyType.Name);
            var xAxisType = xAxisProp.PropertyType;
            var xAxisMethods = xAxisType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            Console.WriteLine("XAxis methods:");
            foreach (var m in xAxisMethods.OrderBy(x => x.Name))
                Console.WriteLine("  " + m.Name + " -> " + string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)));
        }
    }
}
