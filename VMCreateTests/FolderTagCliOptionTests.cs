using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.CommandLine;
using System.Linq;
using VMCreate.CLI.Commands;
using VMCreate.Metadata;

namespace VMCreate.Tests
{
    /// <summary>Parse-level contract of the VMCreate Manager <c>--folder</c> / <c>--tag</c> options.</summary>
    [TestClass]
    public sealed class FolderTagCliOptionTests
    {
        private static ParseResult Parse(params string[] args)
        {
            var parse = CreateCommand.Build(new ServiceCollection().BuildServiceProvider()).Parse(args);
            Assert.AreEqual(0, parse.Errors.Count);
            return parse;
        }

        private static T? Value<T>(ParseResult parse, string name)
            => parse.GetValue((Option<T>)parse.CommandResult.Command.Options.Single(o => o.Name == name));

        [TestMethod]
        public void Folder_IsParsed()
        {
            Assert.AreEqual("Engagements/ACME", Value<string>(Parse("--image", "kali", "--folder", "Engagements/ACME"), "--folder"));
            Assert.IsNull(Value<string>(Parse("--image", "kali"), "--folder"));
        }

        [TestMethod]
        public void Tag_IsRepeatableAndAcceptsCommaLists()
        {
            var raw = Value<string[]>(Parse("--image", "kali", "--tag", "kali", "--tag", "acme, external"), "--tag")!;
            CollectionAssert.AreEqual(new[] { "acme", "external", "kali" },
                MetadataOperations.ParseTags(string.Join(",", raw)));
        }
    }
}
