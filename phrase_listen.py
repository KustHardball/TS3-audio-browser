# -*- coding: utf-8 -*-
"""Слушает короткие куски речи и печатает распознанный текст. Модель грузится один раз."""
from __future__ import annotations

import os
import struct
import sys
from pathlib import Path

# CTranslate2 ищет CUDA рядом с процессом. Библиотеки лежат в сборке PyTorch.
_torch_lib = Path(sys.prefix) / "Lib" / "site-packages" / "torch" / "lib"
if _torch_lib.is_dir():
    os.add_dll_directory(str(_torch_lib))
    os.environ["PATH"] = str(_torch_lib) + os.pathsep + os.environ.get("PATH", "")

import numpy as np
from faster_whisper import WhisperModel

PROMPT_ID = 0xFFFFFFFF


def emit(line: str) -> None:
    sys.stdout.buffer.write((line + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def model_source() -> str:
    local = Path(__file__).resolve().parent / "models" / "large-v3-turbo"
    if (local / "model.bin").is_file():
        return str(local)
    return "large-v3-turbo"


def main() -> None:
    prompt = ""
    try:
        model = WhisperModel(
            model_source(),
            device="cuda",
            compute_type="float16",
            download_root=str(Path(__file__).resolve().parent / "models"),
        )
    except Exception as ex:
        emit("error\t" + str(ex).replace("\n", " "))
        return

    emit("ready")
    stdin = sys.stdin.buffer
    while True:
        header = stdin.read(8)
        if header is None or len(header) < 8:
            return
        client_id, nbytes = struct.unpack("<II", header)
        payload = stdin.read(nbytes) if nbytes else b""
        if len(payload) < nbytes:
            return

        if client_id == PROMPT_ID:
            prompt = payload.decode("utf-8", errors="ignore").strip()
            emit("ok")
            continue

        if nbytes < 2:
            emit(f"{client_id}\t")
            continue

        audio = np.frombuffer(payload, dtype=np.int16).astype(np.float32) / 32768.0
        try:
            # Список фраз в подсказку не кладём: на шуме модель начинает сама их произносить.
            segments, _info = model.transcribe(
                audio,
                language="ru",
                beam_size=1,
                best_of=1,
                vad_filter=False,
                without_timestamps=True,
                condition_on_previous_text=False,
                temperature=0.0,
                initial_prompt=None,
                chunk_length=4,
                log_prob_threshold=-1.0,
                no_speech_threshold=0.6,
                compression_ratio_threshold=2.4,
            )
            parts = list(segments)
            text = " ".join(segment.text for segment in parts).strip()
            logprob = parts[-1].avg_logprob if parts else -9.0
            nospeech = parts[-1].no_speech_prob if parts else 1.0
        except Exception as ex:
            emit(f"error\t{ex}".replace("\n", " "))
            continue
        emit(f"{client_id}\t{text}\t{logprob:.3f}\t{nospeech:.3f}")


if __name__ == "__main__":
    main()
