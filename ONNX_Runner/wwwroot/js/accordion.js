// Animates advanced <details> groups while keeping their native open state synchronized.
document.addEventListener('DOMContentLoaded', () => {
    const accordions = document.querySelectorAll('details.advanced-group');

    accordions.forEach(details => {
        const summary = details.querySelector('summary');
        const content = details.querySelector('.group-content');
        let animation = null;
        let isClosing = false;
        let isExpanding = false;

        summary.addEventListener('click', (e) => {
            // Keep the native toggle under animation control.
            e.preventDefault();
            details.style.overflow = 'hidden';

            if (isClosing || !details.open) {
                open();
            } else if (isExpanding || details.open) {
                shrink();
            }
        });

        // Animates the details element down to its summary height before closing it.
        function shrink() {
            isClosing = true;
            const startHeight = `${details.offsetHeight}px`;
            const endHeight = `${summary.offsetHeight}px`;

            if (animation) animation.cancel();

            animation = details.animate({
                height: [startHeight, endHeight]
            }, {
                duration: 250,
                easing: 'cubic-bezier(0.4, 0, 0.2, 1)'
            });

            // Closes the details element after the collapse animation and clears animation state.
            animation.onfinish = () => {
                details.open = false;
                onAnimationFinish();
            };
            // Clears the closing flag when the collapse animation is canceled.
            animation.oncancel = () => isClosing = false;
        }

        // Opens the details element and animates it to the full content height.
        function open() {
            details.style.height = `${details.offsetHeight}px`;
            details.open = true;
            window.requestAnimationFrame(() => {
                isExpanding = true;
                const startHeight = `${details.offsetHeight}px`;
                const endHeight = `${summary.offsetHeight + content.offsetHeight}px`;

                if (animation) animation.cancel();

                animation = details.animate({
                    height: [startHeight, endHeight]
                }, {
                    duration: 250, 
                    easing: 'cubic-bezier(0.4, 0, 0.2, 1)'
                });

                // Clears animation state after expansion finishes.
                animation.onfinish = () => onAnimationFinish();
                // Clears the expanding flag when the expansion animation is canceled.
                animation.oncancel = () => isExpanding = false;
            });
        }

        // Releases animation references and temporary dimensions after a transition.
        function onAnimationFinish() {
            animation = null;
            isClosing = false;
            isExpanding = false;
            details.style.height = details.style.overflow = '';
        }
    });
});