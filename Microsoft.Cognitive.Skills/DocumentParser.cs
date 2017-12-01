﻿using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using BitMiracle.LibTiff.Classic;
using PdfSharp.Pdf.Filters;

namespace Microsoft.Cognitive.Skills
{

    public static class DocumentParser
    {

        public static DocumentMetadata Parse(Stream stream)
        {
            var images = (PdfReader.TestPdfFile(stream) == 0) ?
                GetImagePages(stream) :  // assume it is some kind of image format
                GetPdfPages(stream); // parse the pdf image

            return new DocumentMetadata() {
                 Pages = images
            };
        }


        private static IEnumerable<PageImage> GetImagePages(Stream stream)
        {
            using (Image imageFile = Image.FromStream(stream))
            {
                // rotate the image if needed
                ImageHelper.CheckImageRotate(imageFile);
                FrameDimension frameDimension = new FrameDimension(imageFile.FrameDimensionsList[0]);

                // Gets the number of pages from the tiff image (if multipage) 
                int frameNum = imageFile.GetFrameCount(frameDimension);

                for (int frame = 0; frame < frameNum; frame++)
                {
                    yield return new ImagePageMetadata(imageFile, frameDimension, frame);
                }

            }
        }

        private static IEnumerable<PageImage> GetPdfPages(Stream stream)
        {
            PdfDocument document = PdfReader.Open(stream);

            // Iterate pages
            int pageNum = 0;
            foreach (PdfPage page in document.Pages)
            {
                pageNum++;
                // Get resources dictionary
                PdfDictionary resources = page.Elements.GetDictionary("/Resources");
                if (resources != null)
                {
                    // Get external objects dictionary
                    PdfDictionary xObjects = resources.Elements.GetDictionary("/XObject");
                    if (xObjects != null)
                    {
                        ICollection<PdfItem> items = xObjects.Elements.Values;
                        // Iterate references to external objects
                        foreach (PdfItem item in items)
                        {
                            PdfReference reference = item as PdfReference;
                            if (reference != null)
                            {
                                PdfDictionary xObject = reference.Value as PdfDictionary;
                                // Is external object an image?
                                if (xObject != null && xObject.Elements.GetString("/Subtype") == "/Image")
                                {
                                    yield return new PdfPageMetadata(document, xObject, pageNum);
                                }
                            }
                        }
                    }
                }
            }
        }

        private class ImagePageMetadata : PageImage
        {
            int frame;
            Image image;
            FrameDimension frameDimension;

            public ImagePageMetadata(Image image, FrameDimension frameDimension, int frame)
            {
                this.image = image;
                this.frameDimension = frameDimension;
                this.frame = frame;
                PageNumber = frame;
            }


            public override Bitmap GetImage()
            {
                image.SelectActiveFrame(frameDimension, frame);
                return new Bitmap(image);
            }
        }



        private class PdfPageMetadata : PageImage
        {
            PdfDocument document;
            PdfDictionary image;

            public PdfPageMetadata(PdfDocument document, PdfDictionary image, int pageNumber)
            {
                this.document = document;
                this.image = image;
                PageNumber = pageNumber;
            }


            public override Bitmap GetImage()
            {
                // get the stream bytes
                byte[] imgData = image.Stream.Value;

                // sometimes an image can be dual encoded, if so decode the first layer
                var filters = image.Elements.GetArray("/Filter");
                string filter;
                if (filters != null && filters.Elements.GetName(0) == "/FlateDecode")
                {
                    // FlateDecode
                    imgData = new FlateDecode().Decode(image.Stream.Value);
                    filter = filters.Elements.GetName(1);
                }
                else if (filters != null && filters.Elements.Count == 1)
                    filter = filters.Elements.GetName(0);
                else
                    filter = image.Elements.GetName("/Filter");

                switch (filter)
                {
                    case "/FlateDecode":  // ?
                        imgData = new FlateDecode().Decode(image.Stream.Value);
                        break;

                    case "/DCTDecode":  // JPEG format
                                        // nativly supported by PDF so nothing to do here
                        break;

                    case "/CCITTFaxDecode":  // TIFF format

                        MemoryStream m = new MemoryStream();

                        Tiff tiff = Tiff.ClientOpen("custom", "w", m, new TiffStream());
                        tiff.SetField(TiffTag.IMAGEWIDTH, image.Elements.GetInteger("/Width"));
                        tiff.SetField(TiffTag.IMAGELENGTH, image.Elements.GetInteger("/Height"));
                        tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
                        tiff.SetField(TiffTag.BITSPERSAMPLE, image.Elements.GetInteger("/BitsPerComponent"));
                        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                        tiff.WriteRawStrip(0, imgData, imgData.Length);
                        tiff.Close();
                        imgData = m.ToArray();
                        break;

                    case "/JBIG2Decode":
                        var d = new JBig2Decoder.JBIG2StreamDecoder();

                        var decodeParams = image.Elements.GetDictionary("/DecodeParms");
                        if (decodeParams != null)
                        {
                            var globalRef = decodeParams.Elements.GetObject("/JBIG2Globals");
                            if (globalRef != null)
                            {
                                var globals = document.Internals.GetObject(globalRef.Reference.ObjectID) as PdfDictionary;
                                d.setGlobalData(globals.Stream.Value);

                            }
                        }

                        imgData = d.decodeJBIG2(imgData);
                        break;

                    default:
                        throw new Exception("Dont know how to decode PDF image type of " + filter);
                }

                return ImageHelper.ConvertTiffToBmps(new MemoryStream(imgData)).First();
            }
        }
    }



    public class DocumentMetadata
    {
        // More document Metadata goes here

        public IEnumerable<PageImage> Pages { get; set; }
    }

    public abstract class PageImage
    {
        public int PageNumber { get; set; }

        public string Id { get; set; }

        public abstract Bitmap GetImage();
    }
}
