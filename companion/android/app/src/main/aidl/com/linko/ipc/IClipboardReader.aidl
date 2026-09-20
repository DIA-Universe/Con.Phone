package com.linko.ipc;
import android.os.Bundle;
interface IClipboardReader {
    Bundle readClipboard() = 0;
    void destroy() = 16777114;
}
