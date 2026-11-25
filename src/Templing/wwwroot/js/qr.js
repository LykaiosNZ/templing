// QR codes for pairing links, drawn as one SVG path (Components/PairDialog.razor). The encoder is qrcode-generator
// (MIT, vendored in lib/qrcode).
import qrcode from '../lib/qrcode/qrcode.js';

/** The dark modules of a QR code for `text`, as an SVG path over a size × size grid (no quiet zone). */
export function path(text) {
    const qr = qrcode(0, 'M');
    qr.addData(text);
    qr.make();
    const size = qr.getModuleCount();
    let d = '';
    for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
            if (qr.isDark(y, x)) d += `M${x} ${y}h1v1h-1z`;
        }
    }
    return JSON.stringify({ size, d });
}
