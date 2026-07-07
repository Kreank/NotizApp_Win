# Codex-Container für die Bildgenerierung der NotizApp (analog zum Claude-Container).
#
# Isolation = Datenschutz: Der Container bekommt KEINE Notiz-Dateien gemountet.
# Die App übergibt pro Anfrage nur den Bild-Auftragstext — keine Notizen.
# Persistiert wird nur das Codex-Login (OpenAI-Abo) im Volume
# "notizapp-codex-config" (gemountet auf /home/codex).
#
# Die vom imagegen-Werkzeug erzeugten PNGs landen unter
# ~/.codex/generated_images/<session>/; die App mountet ihren Ausgabeordner
# genau dorthin und holt die Bilder direkt vom Host ab.
FROM node:22-slim

# curl/ca-certificates: Codex-CLI-Binary lädt/prüft Zertifikate beim Login
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g @openai/codex \
    && useradd -m -s /bin/bash codex

USER codex
ENV HOME=/home/codex
WORKDIR /home/codex/arbeit

ENTRYPOINT ["codex"]
