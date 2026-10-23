using System.Text;
using SweetEmu.Core.Boot;

namespace SweetEmu.Tests;

public static class BootTests
{
    /// The app parses the IPSW's D22AP tree and then the generated tree in one
    /// process (Start). A shared parse cursor once made the second parse fail.
    [Test]
    public static void AdtParsesRepeatedlyInOneProcess()
    {
        var product = new AdtNode();
        product.Props.Add(("name", 0, Encoding.ASCII.GetBytes("product\0")));
        product.Props.Add(("product-name", 0, Encoding.ASCII.GetBytes("iPhone X\0")));
        var d22 = new AdtNode();
        d22.Props.Add(("name", 0, Encoding.ASCII.GetBytes("device-tree\0")));
        d22.Children.Add(product);
        byte[] d22Bytes = d22.Serialize();

        for (int i = 0; i < 3; i++)
        {
            var adt = VirtAdt.Build(d22Bytes);
            var root = AdtNode.Parse(adt);
            if (root.Child("chosen")?.Prop("dram-base") == null) throw new Exception("no /chosen dram-base");
            if (root.Child("product")?.Prop("product-name") == null) throw new Exception("no /product copied");
        }
    }
}
