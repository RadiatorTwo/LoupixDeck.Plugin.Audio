# Plugin icon generator

`make_icon.py` draws the plugin icon (variant 3a: knob with level ring, matte, night blue). It was
created by the designer; the icon is original artwork, no third-party source.

```bash
pip install pillow numpy
python tools/icon/make_icon.py tools/icon/out
cp tools/icon/out/icon_3a_256.png icon.png
```

The script writes `icon_3a_{256,128,64,32,16}.png` into the given folder; only the 256 px file is
used, as `icon.png` in the repo root. The `out/` folder is not committed.
