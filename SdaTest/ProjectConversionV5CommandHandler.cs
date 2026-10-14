using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DotNetSiemensPLCToolBoxLibrary.DataTypes;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Blocks;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Blocks.Step5;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Blocks.Step7V5;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Projectfolders;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Projectfolders.Step5;
using DotNetSiemensPLCToolBoxLibrary.Projectfiles;

namespace SdaTest
{
    /// <summary>
    /// Project conversion for Siemens Step 5 projects (.s5d, or a zip containing one).
    /// Produces the same output layout as <see cref="ProjectConversionCommandHandler"/>, limited to what
    /// a Step 5 project contains: blocks and the symbol table. There is no hardware configuration and no sources.
    /// </summary>
    public class ProjectConversionV5CommandHandler
    {
        private static string _outputDir = "";
        private const string SymbolsDir = "symbols";
        private const string BlocksDir = "blocks";

        // Pre-headers (DV, FV, ...) and comment blocks (DK, FK, PK, ...) are merged into these by the library
        private static readonly HashSet<PLCBlockType> ExportedBlockTypes = new()
        {
            PLCBlockType.S5_OB, PLCBlockType.S5_PB, PLCBlockType.S5_SB, PLCBlockType.S5_FB,
            PLCBlockType.S5_FX, PLCBlockType.S5_DB, PLCBlockType.S5_DX
        };

        private static readonly List<string> _failures = new();

        private static void CreateWorkingDirectory(string directory, bool cleanup = false)
        {
            if (cleanup && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            Directory.CreateDirectory(directory);
        }

        private static void AddFailure(string scope, string name, string errorMessage)
        {
            Console.WriteLine($"[FAIL] [{scope}] {name}: {errorMessage}");
            _failures.Add($"{scope}/{name}");
        }

        public static void Export(string projectPath, string outputPath)
        {
            _outputDir = outputPath;
            CreateWorkingDirectory(_outputDir);
            CreateWorkingDirectory(Path.Combine(_outputDir, SymbolsDir), true);
            CreateWorkingDirectory(Path.Combine(_outputDir, BlocksDir), true);

            if (Projects.LoadProject(projectPath, false) is not Step5Project project)
            {
                throw new Exception("ERROR: Project file invalid or corrupted. No s5d file found in the project-archive.");
            }

            var projectItem = new XElement("ProjectItem",
                new XAttribute("Name", "Project"),
                new XAttribute("Type", "Folder"));

            AddXmlNodes(projectItem, project.ProjectStructure.SubItems);

            var xmlDocument = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), projectItem);
            xmlDocument.Save(Path.Combine(outputPath, "project_tree.xml"));

            if (_failures.Count != 0) throw new Exception("Conversion Failed");

            Console.WriteLine("Export finished successfully.");
        }

        private static string HashContent(string content)
        {
            var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsValidXmlChar(char c)
        {
            return c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
        }

        private static string SanitizeXmlString(string input)
        {
            return new string(input.Select(c => IsValidXmlChar(c) ? c : '?').ToArray());
        }

        // Step 5 blocks are ISO-8859-1 binaries, so names, comments and AWL can contain control characters
        private static void SanitizeElementInPlace(XElement element)
        {
            foreach (var attr in element.Attributes())
            {
                attr.Value = SanitizeXmlString(attr.Value);
            }

            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText textNode:
                        textNode.Value = SanitizeXmlString(textNode.Value);
                        break;
                    case XElement childElement:
                        SanitizeElementInPlace(childElement);
                        break;
                }
            }
        }

        /**
         * Create a new file with the given parameters and return the File XML item
         */
        private static XElement CreateFile(XElement item, string subDirectory, string uniqueFileName,
            string contentType)
        {
            var hashedFileName = $"{HashContent(uniqueFileName)}.xml";
            var absoluteFilePath = Path.Combine(_outputDir, subDirectory, hashedFileName);
            var xmlRelativeFilePath = $"{subDirectory}/{hashedFileName}";

            SanitizeElementInPlace(item);
            var newXmlContent = new XDocument(item).ToString();

            File.WriteAllText(absoluteFilePath, newXmlContent);

            return new XElement("File", new XAttribute("DiffHash", HashContent(newXmlContent)),
                new XAttribute("ContentType", contentType),
                xmlRelativeFilePath);
        }

        private static XElement CreateItemElement(string name, string type)
        {
            return new XElement("Item", new XAttribute("Name", name), new XAttribute("Type", type));
        }

        private static void AddInterfaceParameters(XElement rootXml, List<S5Parameter> parameters)
        {
            var interfaceItem = new XElement("interface");

            foreach (var parameter in parameters)
            {
                var varItem = new XElement("variable", new XAttribute("Name", parameter.Name));
                varItem.Add(new XElement("dataType", $"{parameter.S5ParameterType}/{parameter.S5ParameterFormat}"));

                if (!string.IsNullOrEmpty(parameter.Comment))
                {
                    varItem.Add(new XElement("comment", parameter.Comment));
                }

                interfaceItem.Add(varItem);
            }

            rootXml.Add(interfaceItem);
        }

        // S5 data blocks are converted into S7DataRows by the library
        private static void AddInterfaceParameters(XElement rootXml, List<IDataRow> parameters)
        {
            foreach (var parameter in parameters)
            {
                var varItem = new XElement("variable", new XAttribute("Name", parameter.Name));
                varItem.Add(new XElement("dataType", parameter.DataType.ToString()));

                if (!string.IsNullOrEmpty(parameter.Comment))
                {
                    varItem.Add(new XElement("comment", parameter.Comment));
                }

                varItem.Add(new XElement("address", parameter.BlockAddress.ToString()));

                // MC5toDB fills Value, not StartValue: a S5 DB has no separate start values. Its default-value
                // fallback (int 0) cannot be unboxed to the S5 types, so only rows with a parsed Value are exported.
                if (parameter is S7DataRow { Value: not null } s7DataRowParameter)
                {
                    varItem.Add(new XElement("value", s7DataRowParameter.ValueAsString));
                }

                AddInterfaceParameters(varItem, parameter.Children);

                rootXml.Add(varItem);
            }
        }

        private static void AddBlocks(XElement rootXml, Step5BlocksFolder blocksFolder)
        {
            foreach (var blockInfo in blocksFolder.BlockInfos.Where(b => ExportedBlockTypes.Contains(b.BlockType)))
            {
                var blockName = ((S5ProjectBlockInfo)blockInfo).BlockName;
                Console.WriteLine("Convert block " + blockName);
                try
                {
                    var block = blockInfo.GetBlock();
                    var blockType = block.GetType().Name;
                    var blockItem = CreateItemElement(block.BlockName, blockType);
                    var blockContent = new XElement(blockType, new XAttribute("Name", block.BlockName),
                        new XAttribute("Type", blockType));

                    switch (block)
                    {
                        case S5FunctionBlock s5FunctionBlock:
                            // Only FBs/FXs carry a name in Step 5
                            if (!string.IsNullOrEmpty(s5FunctionBlock.Name))
                            {
                                blockContent.Add(new XAttribute("Title", s5FunctionBlock.Name));
                            }

                            if (!string.IsNullOrEmpty(s5FunctionBlock.Description))
                            {
                                blockContent.Add(new XAttribute("Description", s5FunctionBlock.Description));
                            }

                            if (s5FunctionBlock.Parameter != null)
                            {
                                AddInterfaceParameters(blockContent, s5FunctionBlock.Parameter);
                            }

                            var networksItem = new XElement("Networks");

                            foreach (var network in s5FunctionBlock.Networks)
                            {
                                var networkItem = new XElement("Network", new XAttribute("Title", network.Name ?? ""));
                                networkItem.Add(new XElement("comment", network.Comment));
                                networkItem.Add(new XElement("AWL", network.AWLCodeToString()));
                                networksItem.Add(networkItem);
                            }

                            blockContent.Add(networksItem);
                            break;
                        case S5DataBlock s5DataBlock:
                            AddInterfaceParameters(blockContent, s5DataBlock.Structure.Children);
                            break;
                    }

                    var uniqueBlockName = blocksFolder.StructuredFolderName + "/" + block.BlockName;
                    blockItem.Add(CreateFile(blockContent, BlocksDir, uniqueBlockName, blockType));

                    rootXml.Add(blockItem);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    AddFailure("blocks", blockName, ex.Message);
                }
            }
        }

        private static void AddSymbols(XElement rootXml, SymbolTable symbolTable)
        {
            var symbolTableType = $"S5{symbolTable.GetType().Name}";
            var symbolTableContent = new XElement(symbolTableType, new XAttribute("Name", symbolTable.Name),
                new XAttribute("Type", symbolTableType));

            // Set the type of the parent item
            rootXml.SetAttributeValue("Type", symbolTableType);

            var symbolEntries = symbolTable.SymbolTableEntrys;
            symbolEntries.Sort((x, y) => string.Compare(x.Symbol, y.Symbol, StringComparison.Ordinal));

            foreach (var symbolTableEntry in symbolEntries)
            {
                // The Step 5 symbol table (Z0.SEQ) has no data type, and the comment column is optional
                symbolTableContent.Add(new XElement("symbol",
                    new XAttribute("Symbol", symbolTableEntry.Symbol),
                    new XAttribute("Comment", symbolTableEntry.Comment ?? ""),
                    new XAttribute("Address", symbolTableEntry.Operand)));
            }

            rootXml.Add(CreateFile(symbolTableContent, SymbolsDir, symbolTable.StructuredFolderName, symbolTableType));
        }

        private static void AddXmlNodes(XElement rootXml, List<ProjectFolder> items)
        {
            foreach (var subitem in items)
            {
                // Reference data is derived from the blocks, not project content
                if (subitem is ReferenceData)
                {
                    continue;
                }

                var subItemElement = CreateItemElement(subitem.Name, "item");

                switch (subitem)
                {
                    case Step5BlocksFolder blocksFolder:
                        AddBlocks(subItemElement, blocksFolder);
                        break;
                    case SymbolTable symbolTable:
                        AddSymbols(subItemElement, symbolTable);
                        break;
                }

                rootXml.Add(subItemElement);
            }
        }
    }
}
