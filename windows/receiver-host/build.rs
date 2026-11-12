fn main() {
    let pointer = "native/tablet_pointer.cpp";
    let rfcomm = "native/tablet_rfcomm.cpp";
    println!("cargo:rerun-if-changed={pointer}");
    println!("cargo:rerun-if-changed={rfcomm}");
    cc::Build::new()
        .cpp(true)
        .std("c++17")
        .file(pointer)
        .file(rfcomm)
        .compile("pennote_native");
    println!("cargo:rustc-link-lib=user32");
    println!("cargo:rustc-link-lib=ws2_32");
}
