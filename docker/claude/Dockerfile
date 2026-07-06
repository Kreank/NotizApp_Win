# Claude-Container für die NotizApp-KI-Funktionen (V2).
#
# Isolation = Datenschutz: Der Container bekommt KEINE Notiz-Dateien gemountet.
# Die App übergibt pro Anfrage nur den Notiz-Body (ohne Frontmatter/Kundendaten)
# über stdin. Persistiert wird nur das Claude-Login im Volume
# "notizapp-claude-config" (gemountet auf /home/claude).
FROM node:22-slim

# pandoc + weasyprint: Markdown/HTML -> PDF fuer die Dokument-Erzeugung
# curl: Bilder/Dateien aus dem Netz laden (Auftrag "such mir Bilder zu ...")
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       pandoc weasyprint fonts-dejavu curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g @anthropic-ai/claude-code \
    && useradd -m -s /bin/bash claude

USER claude
ENV HOME=/home/claude
WORKDIR /home/claude/arbeit

ENTRYPOINT ["claude"]
