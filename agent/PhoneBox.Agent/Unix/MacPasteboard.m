#import <Foundation/Foundation.h>
#import <objc/message.h>
#include <stdint.h>

// Match MacPasteboard.Call in the managed wrapper. Return only success and the result to managed code.
typedef enum {
    PBCallPointer, PBCallPointerWithArgument, PBCallPointerWithThreeArguments, PBCallInteger, PBCallSize, PBCallBooleanOne, PBCallBooleanTwo
} PasteboardCall;

int phonebox_pasteboard_invoke(void *receiver, SEL selector, uintptr_t a, uintptr_t b, uintptr_t c,
                              PasteboardCall call, uintptr_t *result)
{
    id target = (id)receiver;
    @try {
        switch (call) {
            case PBCallPointer:
                *result = (uintptr_t)((id (*)(id, SEL))objc_msgSend)(target, selector);
                break;
            case PBCallPointerWithArgument:
                *result = (uintptr_t)((id (*)(id, SEL, uintptr_t))objc_msgSend)(target, selector, a);
                break;
            case PBCallPointerWithThreeArguments:
                *result = (uintptr_t)((id (*)(id, SEL, const void *, NSUInteger, NSUInteger))objc_msgSend)
                    (target, selector, (const void *)a, b, c);
                break;
            case PBCallInteger:
                *result = (uintptr_t)((NSInteger (*)(id, SEL))objc_msgSend)(target, selector);
                break;
            case PBCallSize:
                *result = ((NSUInteger (*)(id, SEL, NSUInteger))objc_msgSend)(target, selector, a);
                break;
            case PBCallBooleanOne:
                *result = ((BOOL (*)(id, SEL, SEL))objc_msgSend)(target, selector, (SEL)a) != NO;
                break;
            case PBCallBooleanTwo:
                *result = ((BOOL (*)(id, SEL, id, id))objc_msgSend)(target, selector, (id)a, (id)b) != NO;
                break;
            default:
                return 0;
        }
        return 1;
    } @catch (NSException *exception) {
        // In particular NSPasteboardCommunicationException must never unwind into managed frames.
        (void)exception;
        *result = 0;
        return 0;
    }
}
