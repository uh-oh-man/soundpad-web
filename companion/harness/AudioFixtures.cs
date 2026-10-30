namespace SoundpadWeb.Testing;

public static class AudioFixtures
{
    public static void Wave(string path, int milliseconds = 2500) { using var output = new BinaryWriter(File.Create(path)); int frames = 48000 * milliseconds / 1000; output.Write("RIFF"u8); output.Write(36 + frames * 4); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)1); output.Write((short)2); output.Write(48000); output.Write(48000 * 4); output.Write((short)4); output.Write((short)16); output.Write("data"u8); output.Write(frames * 4); for (int i = 0; i < frames; i++) { short sample = i < frames / 4 ? (short)0 : (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 25000 * (0.2 + 0.8 * Math.Pow(Math.Sin(i * 2 * Math.PI * 1.7 / 48000), 2))); output.Write(sample); output.Write((short)-sample); } }
}
