# Copy Casey's supplied PNG unchanged; resize only separate Windows icon frames.
# Preserve the complete canvas and aspect ratio. Never extract/redraw the artwork.
param([string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Logo\Rocket logo.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class LaunchPadLogoAssets {
    public static string Create(string input, string png, string ico) {
        using (var source = new Bitmap(input)) {
            if(source.RawFormat.Guid!=ImageFormat.Png.Guid) throw new InvalidDataException("Supply the original PNG.");
            File.Copy(input,png,true);
            {
                var sizes=new int[]{16,24,32,48,64,128,256};
                var payload=new List<byte[]>();
                foreach(int size in sizes) using(var image=new Bitmap(size,size,PixelFormat.Format32bppArgb)) {
                    using(var graphics=Graphics.FromImage(image)) {
                        graphics.Clear(Color.Transparent);
                        graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
                        double scale=Math.Min((double)size/source.Width,(double)size/source.Height);
                        int width=Math.Max(1,(int)Math.Round(source.Width*scale));
                        int height=Math.Max(1,(int)Math.Round(source.Height*scale));
                        graphics.DrawImage(source,new Rectangle((size-width)/2,(size-height)/2,width,height));
                    }
                    using(var stream=new MemoryStream()) { image.Save(stream,ImageFormat.Png); payload.Add(stream.ToArray()); }
                }
                using(var stream=new FileStream(ico,FileMode.Create,FileAccess.Write)) using(var writer=new BinaryWriter(stream)) {
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                    int offset=6+16*sizes.Length;
                    for(int i=0;i<sizes.Length;i++) {
                        writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                        writer.Write(payload[i].Length); writer.Write(offset); offset+=payload[i].Length;
                    }
                    foreach(var item in payload) writer.Write(item);
                    writer.Flush(); stream.Flush(true);
                }
            }
            return source.Width+"x"+source.Height+" (unchanged PNG)";
        }
    }
}
'@
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LaunchPad\Assets'
$png = Join-Path $assets 'launchpad-rocket.png'
$ico = Join-Path $assets 'LaunchPad.ico'
$bounds = [LaunchPadLogoAssets]::Create((Resolve-Path -LiteralPath $Source).Path, $png, $ico)
[pscustomobject]@{source=$Source;artworkBounds=$bounds;png=$png;icon=$ico;sizes=@(16,24,32,48,64,128,256)}
