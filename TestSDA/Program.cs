using System;
using SdaTesting;

namespace TestSDA
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("Testing...");
            var projectPath = "/Users/tom/Downloads/Simatic S7_AWL_OP10.zip";
            var exportPath = "/Users/tom/Downloads/export";
            ProjectConversionCommandHandler.Export(projectPath, exportPath);
        }
    }
}