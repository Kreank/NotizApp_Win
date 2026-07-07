using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NotizApp.Services;

/// <summary>
/// Gesprächs-Aufnahme: Standard-Mikrofon UND System-/PC-Ton (WASAPI-Loopback),
/// damit bei Telefon-/Teams-Gesprächen beide Seiten aufgezeichnet werden.
/// Beide Spuren landen als temporäre WAVs im Scratch-Ordner; StoppeAsync mischt
/// sie auf 16 kHz mono 16 bit (das Format, das Whisper erwartet) und liefert
/// den Pfad der fertigen Misch-WAV.
///
/// Loopback darf fehlschlagen (z.B. kein aktives Ausgabegerät) — dann wird
/// nur das Mikrofon aufgenommen, ohne Fehler. Loopback liefert außerdem nur
/// Daten, solange tatsächlich Ton abgespielt wird; Stille-Lücken sind ok,
/// beide Spuren werden einfach ab 0 gemischt (kein exaktes Sync nötig).
/// </summary>
public class AufnahmeService : IDisposable
{
    static string ScratchOrdner => Path.Combine(Path.GetTempPath(), "NotizApp-Aufnahme");

    WasapiCapture? _mikrofon;
    WasapiLoopbackCapture? _loopback;
    WaveFileWriter? _mikrofonWriter;
    WaveFileWriter? _loopbackWriter;
    string? _mikrofonPfad;
    string? _loopbackPfad;

    public bool LaeuftAufnahme { get; private set; }

    /// <summary>Aufnahme starten. Wirft, wenn kein Mikrofon verfügbar ist.</summary>
    public void Starte()
    {
        if (LaeuftAufnahme) return;
        Directory.CreateDirectory(ScratchOrdner);
        var stempel = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // Mikrofon (Standard-Aufnahmegerät) — Pflicht, ohne geht nichts
        try
        {
            _mikrofonPfad = Path.Combine(ScratchOrdner, $"{stempel}-mikrofon.wav");
            _mikrofon = new WasapiCapture();
            _mikrofonWriter = new WaveFileWriter(_mikrofonPfad, _mikrofon.WaveFormat);
            _mikrofon.DataAvailable += (_, e) =>
                _mikrofonWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _mikrofon.StartRecording();
        }
        catch
        {
            RaeumeGeraeteAuf();
            throw;
        }

        // System-Ton (Loopback) — optional, scheitert leise
        try
        {
            _loopbackPfad = Path.Combine(ScratchOrdner, $"{stempel}-system.wav");
            _loopback = new WasapiLoopbackCapture();
            _loopbackWriter = new WaveFileWriter(_loopbackPfad, _loopback.WaveFormat);
            _loopback.DataAvailable += (_, e) =>
                _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _loopback.StartRecording();
        }
        catch
        {
            _loopbackWriter?.Dispose();
            _loopbackWriter = null;
            _loopback?.Dispose();
            _loopback = null;
            _loopbackPfad = null;
        }

        LaeuftAufnahme = true;
    }

    /// <summary>
    /// Aufnahme beenden, beide Spuren auf 16 kHz mono mischen und den Pfad
    /// der fertigen Misch-WAV liefern (Name eignet sich als Anhang-Name).
    /// </summary>
    public async Task<string> StoppeAsync(CancellationToken ct)
    {
        if (!LaeuftAufnahme)
            throw new InvalidOperationException("Es läuft keine Aufnahme.");
        LaeuftAufnahme = false;

        await Task.WhenAll(StoppeGeraetAsync(_mikrofon), StoppeGeraetAsync(_loopback));

        // Writer schließen, damit die WAV-Header geschrieben sind
        _mikrofonWriter?.Dispose();
        _mikrofonWriter = null;
        _loopbackWriter?.Dispose();
        _loopbackWriter = null;

        var mikrofonPfad = _mikrofonPfad!;
        var loopbackPfad = _loopbackPfad;
        RaeumeGeraeteAuf();

        var ziel = Path.Combine(ScratchOrdner,
            $"aufnahme-{DateTime.Now:yyyy-MM-dd-HHmm}.wav");
        return await Task.Run(() => Mische(mikrofonPfad, loopbackPfad, ziel), ct);
    }

    /// <summary>StopRecording anstoßen und RecordingStopped abwarten (max. 5 s).</summary>
    static Task StoppeGeraetAsync(WasapiCapture? geraet)
    {
        if (geraet is null) return Task.CompletedTask;
        var fertig = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        geraet.RecordingStopped += (_, _) => fertig.TrySetResult();
        try
        {
            geraet.StopRecording();
        }
        catch
        {
            return Task.CompletedTask; // Gerät schon weg → nichts mehr abzuwarten
        }
        return Task.WhenAny(fertig.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// Beide Spuren mischen: je Spur ggf. Stereo→Mono, auf 16 kHz resampeln,
    /// dann zusammen als 16-bit-WAV schreiben. Fehlt die Loopback-Spur (oder
    /// blieb sie leer), kommt nur das Mikrofon in die Mischung.
    /// </summary>
    static string Mische(string mikrofonPfad, string? loopbackPfad, string zielPfad)
    {
        var leser = new List<AudioFileReader>();
        try
        {
            var spuren = new List<ISampleProvider>();
            foreach (var pfad in new[] { mikrofonPfad, loopbackPfad })
            {
                if (pfad is null || !File.Exists(pfad)) continue;
                var reader = new AudioFileReader(pfad);
                if (reader.Length == 0) { reader.Dispose(); continue; }
                leser.Add(reader);

                ISampleProvider spur = reader;
                if (spur.WaveFormat.Channels == 2)
                    spur = new StereoToMonoSampleProvider(spur);
                spuren.Add(new WdlResamplingSampleProvider(spur, 16000));
            }
            if (spuren.Count == 0)
                throw new InvalidOperationException(
                    "Die Aufnahme enthält keine Audiodaten (Mikrofon stumm?).");

            var mixer = new MixingSampleProvider(spuren); // endet mit der längsten Spur
            WaveFileWriter.CreateWaveFile16(zielPfad, mixer);
            return zielPfad;
        }
        finally
        {
            foreach (var r in leser) r.Dispose();
            // Die Roh-Spuren werden nicht mehr gebraucht
            LoescheLeise(mikrofonPfad);
            LoescheLeise(loopbackPfad);
        }
    }

    static void LoescheLeise(string? pfad)
    {
        if (pfad is null) return;
        try { File.Delete(pfad); } catch { }
    }

    void RaeumeGeraeteAuf()
    {
        _mikrofonWriter?.Dispose();
        _mikrofonWriter = null;
        _loopbackWriter?.Dispose();
        _loopbackWriter = null;
        _mikrofon?.Dispose();
        _mikrofon = null;
        _loopback?.Dispose();
        _loopback = null;
    }

    public void Dispose()
    {
        if (LaeuftAufnahme)
        {
            LaeuftAufnahme = false;
            try { _mikrofon?.StopRecording(); } catch { }
            try { _loopback?.StopRecording(); } catch { }
        }
        RaeumeGeraeteAuf();
        GC.SuppressFinalize(this);
    }
}
