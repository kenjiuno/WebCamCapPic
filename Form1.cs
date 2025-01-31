﻿using MediaFoundation;
using MediaFoundation.Misc;
using MediaFoundation.ReadWrite;
using MediaFoundation.Transform;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebCamCapPic.Properties;
using WebCamCapPic.Utils;

namespace WebCamCapPic
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        class DeviceItem
        {
            public string Name { get; internal set; }
            public Guid Type { get; internal set; }
            public string SymLink { get; internal set; }

            public override string ToString() => Name;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // http://codeit.blog.fc2.com/blog-entry-5.html
            MF.EnumVideoDeviceSources(out IMFActivate[] devices);
            foreach (var device in devices)
            {
                device.GetString(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out string name);
                device.GetString(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, out string symLink);
                device.GetGUID(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, out Guid type);
                devicesCombo.Items.Add(
                    new DeviceItem
                    {
                        Name = name,
                        Type = type,
                        SymLink = symLink,
                    }
                );
            }

            savePerSecText_TextChanged(sender, e);
        }

        private string GetSubTypeName(Guid subType)
        {
            foreach (var field in typeof(MFMediaType).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                if (field.FieldType == typeof(Guid))
                {
                    if ((Guid)field.GetValue(null) == subType)
                    {
                        return field.Name;
                    }
                }
            }
            return null;
        }

        private void devicesCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            var item = (DeviceItem)devicesCombo.SelectedItem;
            if (item != null)
            {
                mediaCombo.Items.Clear();

                using (var releaser = new ComReleaser())
                {
                    MF.CreateVideoDeviceSource(item.SymLink, out IMFMediaSource source);
                    releaser.Add(source);
                    source.CreatePresentationDescriptor(out IMFPresentationDescriptor presDesc);
                    releaser.Add(presDesc);
                    presDesc.GetStreamDescriptorCount(out int descCount);
                    for (int descIndex = 0; descIndex < descCount; descIndex++)
                    {
                        presDesc.GetStreamDescriptorByIndex(descIndex, out bool selected, out IMFStreamDescriptor strmDesc);
                        releaser.Add(strmDesc);
                        strmDesc.GetMediaTypeHandler(out IMFMediaTypeHandler handler);
                        releaser.Add(handler);
                        handler.GetMediaTypeCount(out int typeCount);
                        for (int typeIndex = 0; typeIndex < typeCount; typeIndex++)
                        {
                            handler.GetMediaTypeByIndex(typeIndex, out IMFMediaType type);
                            releaser.Add(type);
                            type.GetSize(MFAttributesClsid.MF_MT_FRAME_SIZE, out uint width, out uint height);
                            type.GetGUID(MFAttributesClsid.MF_MT_SUBTYPE, out Guid subType);
                            type.GetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, out uint stride);
                            type.GetUINT32(MFAttributesClsid.MF_MT_SAMPLE_SIZE, out uint sampleSize);

                            mediaCombo.Items.Add(
                                new MediaItem
                                {
                                    Name = $"#{descIndex}.{typeIndex}: {width}x{height}, {GetSubTypeName(subType)}, {((int)stride)}, {sampleSize}",
                                    DescIndex = descIndex,
                                    TypeIndex = typeIndex,
                                    Width = (int)width,
                                    Height = (int)height,
                                    Stride = (int)stride,
                                    SampleSize = (int)sampleSize,
                                    DeviceItem = item,
                                    SubType = subType,
                                }
                            );
                        }
                    }
                }
            }
        }

        class MediaItem
        {
            public string Name { get; internal set; }
            public int Width { get; internal set; }
            public int Height { get; internal set; }
            public int Stride { get; internal set; }
            public int SampleSize { get; internal set; }
            public DeviceItem DeviceItem { get; internal set; }
            public int DescIndex { get; internal set; }
            public int TypeIndex { get; internal set; }
            public Guid SubType { get; internal set; }

            public override string ToString() => Name;
        }

        private void mediaCombo_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private async void startBtn_Click(object sender, EventArgs e)
        {
            var item = (MediaItem)mediaCombo.SelectedItem;
            if (item != null)
            {
                startBtn.Enabled = false;
                stopBtn.Enabled = true;
                stopEvent.Reset();
                await Task.Run(() => CaptureStillImages(item));
                startBtn.Enabled = true;
                stopBtn.Enabled = false;
            }
        }

        void UpdatePreview(Bitmap pic)
        {
            preview.Image = pic;
        }

        private void CaptureStillImages(MediaItem item)
        {
            using (var releaser = new ComReleaser())
            {
                MF.CreateVideoDeviceSource(item.DeviceItem.SymLink, out IMFMediaSource source);
                releaser.Add(source);
                source.CreatePresentationDescriptor(out IMFPresentationDescriptor presDesc);
                releaser.Add(presDesc);
                presDesc.GetStreamDescriptorByIndex(item.DescIndex, out bool selected, out IMFStreamDescriptor strmDesc);
                releaser.Add(strmDesc);
                strmDesc.GetMediaTypeHandler(out IMFMediaTypeHandler handler);
                releaser.Add(handler);
                handler.GetMediaTypeByIndex(item.TypeIndex, out IMFMediaType type);
                handler.SetCurrentMediaType(type);

                MF.CreateSourceReaderFromMediaSource(source, out IMFSourceReader reader);
                if (reader == null)
                {
                    return;
                }
                releaser.Add(reader);

                IMFTransform transform = null;
                MFTOutputDataBuffer[] outSamples = null;
                IMFSample outRgb24Sample = null;
                IMFMediaBuffer outRgb24Buffer = null;

                int rgbSize = item.Width * item.Height * 3;

                var needToConvert = item.SubType != MFMediaType.RGB24;
                if (needToConvert)
                {
                    var processor = new VideoProcessorMFT();
                    releaser.Add(processor);
                    transform = (IMFTransform)processor;
                    HR(transform.SetInputType(0, type, MFTSetTypeFlags.None));
                    var rgbMediaType = MF.CreateMediaType();
                    releaser.Add(rgbMediaType);
                    HR(type.CopyAllItems(rgbMediaType));
                    HR(rgbMediaType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.RGB24));
                    HR(rgbMediaType.SetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, 3 * item.Width));
                    HR(rgbMediaType.SetUINT32(MFAttributesClsid.MF_MT_SAMPLE_SIZE, rgbSize));
                    HR(transform.SetOutputType(0, rgbMediaType, MFTSetTypeFlags.None));

                    outSamples = new MFTOutputDataBuffer[1];
                    outSamples[0] = new MFTOutputDataBuffer();
                    outRgb24Sample = MF.CreateSample();
                    releaser.Add(outRgb24Sample);
                    outRgb24Buffer = MF.CreateMemoryBuffer(rgbSize);
                    releaser.Add(outRgb24Buffer);
                    outRgb24Sample.AddBuffer(outRgb24Buffer);
                    outSamples[0].pSample = Marshal.GetIUnknownForObject(outRgb24Sample);
                }

                int frames = 0;
                while (!stopEvent.WaitOne(0))
                {
                    var hrRS = reader.ReadSample(
                        (int)MF_SOURCE_READER.AnyStream,
                        MF_SOURCE_READER_CONTROL_FLAG.None,
                        out int streamIndex,
                        out MF_SOURCE_READER_FLAG flags,
                        out long timeStamp,
                        out IMFSample sample
                    );

                    if (sample != null)
                    {
                        try
                        {
                            IMFSample rgbSample = sample;

                            if (transform != null)
                            {
                                while (true)
                                {
                                    var hrPO = transform.ProcessOutput(
                                        MFTProcessOutputFlags.None,
                                        1,
                                        outSamples,
                                        out ProcessOutputStatus status
                                    );
                                    if (hrPO.Succeeded())
                                    {
                                        ConsumeBuffer(outRgb24Buffer, item);
                                        frames++;
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                var hrPI = transform.ProcessInput(0, sample, 0);
                                continue;
                            }

                            rgbSample.GetBufferByIndex(0, out IMFMediaBuffer buff);
                            if (ConsumeBuffer(buff, item))
                            {
                                frames++;
                            }
                            else
                            {
                                return;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sample);
                        }
                    }
                }
            }
        }

        private bool ConsumeBuffer(IMFMediaBuffer buff, MediaItem item)
        {
            buff.Lock(out IntPtr ptr, out int maxLen, out int curLen);
            try
            {
                Bitmap pic = new Bitmap(item.Width, item.Height, PixelFormat.Format24bppRgb);
                var bitmapData = pic.LockBits(
                    new Rectangle(0, 0, pic.Width, pic.Height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb
                );
                try
                {
                    byte[] temp = new byte[curLen];
                    Marshal.Copy(ptr, temp, 0, temp.Length);
                    Marshal.Copy(temp, 0, bitmapData.Scan0, Math.Min(temp.Length, bitmapData.Stride * bitmapData.Height));
                }
                finally
                {
                    pic.UnlockBits(bitmapData);
                }
                if (item.Stride < 0)
                {
                    pic.RotateFlip(RotateFlipType.RotateNoneFlipY);
                }
                try
                {
                    Invoke((Action<Bitmap>)UpdatePreview, pic);
                    return true;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
            finally
            {
                buff.Unlock();
            }
        }

        private void HR(HResult hr)
        {
            Debug.Assert(hr.Succeeded());
        }

        AutoResetEvent stopEvent = new AutoResetEvent(false);

        private void stopBtn_Click(object sender, EventArgs e)
        {
            stopEvent.Set();
        }

        private void saveDirBtn_Click(object sender, EventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = Path.Combine(Settings.Default.SaveDir ?? ".", "(DIR)"),
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                Settings.Default.SaveDir = Path.GetDirectoryName(sfd.FileName);
                Settings.Default.Save();
            }
        }

        private void savePerSecText_Click(object sender, EventArgs e)
        {

        }

        private void timerSavePic_Tick(object sender, EventArgs e)
        {
            if (preview.Image != null)
            {
                var now = DateTime.Now;
                var dir = Settings.Default.SaveDir ?? ".";
                var filePath = Path.Combine(dir, $"{now:yyyy MM dd HH mm ss}.png");
                preview.Image.Save(filePath, ImageFormat.Png);
                shotLabel.Text = $"{now:ss}";
            }
        }

        private void savePerSecText_TextChanged(object sender, EventArgs e)
        {
            if (int.TryParse(savePerSecText.Text, out int secs))
            {
                timerSavePic.Enabled = false;
                timerSavePic.Interval = 1000 * Math.Max(1, secs);
                timerSavePic.Enabled = true;
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            stopBtn_Click(sender, e);
        }
    }
}
